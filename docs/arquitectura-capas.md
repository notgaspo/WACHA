# Arquitectura y objetos por capa

Qué objeto concreto cruza cada frontera del bot, desde WhatsApp hasta la respuesta.
Referencias `archivo:línea` a código real de este repo.

## El invariante

```
transporte  →  Activity  →  ITurnContext  →  bot  →  IActivity  →  transporte
              (volátil)      (frontera estable)                    (volátil)
```

El bot **nunca ve HTTP** ni sabe que abajo hay WhatsApp. El adapter es la única pieza que
conoce el transporte. Esa separación es la arquitectura entera, y no es teórica: este
proyecto nació hablando con el Bot Framework Emulator vía `CloudAdapter` y hoy habla
WhatsApp vía Azure Communication Services — se reemplazaron el endpoint, el controller y
el adapter, y **la lógica del bot no cambió ni una línea**. El apéndice A cuenta cómo era y
qué sobrevivió exactamente.

## Estructura

Dos proyectos: el adapter es una librería distribuible, el bot es la app que la consume.

```
WhatsAppToAcsChannelAdapter/          ← librería (el channel adapter, publicable como NuGet)
  Controllers/EventGridController.cs    endpoint HTTP: recibe eventos de Event Grid
  Adapters/AcsWhatsAppAdapter.cs        transporte ↔ dominio: corre el turn y envía a ACS
  Adapters/AcsActivityFactory.cs        traducción pura evento ACS → Activity
  Config/AcsOptions.cs                  opciones tipadas (connection string, channel id)

Charly/                               ← app (un bot concreto)
  Bots/BotCharly.cs                     la lógica del bot; no conoce el transporte
  Startup.cs, Program.cs                cableado de DI y host
```

La frontera entre los dos proyectos es deliberada: quien instale la librería no debería
tener que escribir el handshake de Event Grid ni el mapeo a `Activity`. Lo único suyo es el
bot.

> Como el controller vive en el assembly de la librería, ASP.NET no lo descubre solo: hay
> que registrarlo explícitamente con `AddApplicationPart`
> (`Charly/Startup.cs:30-31`). Sin eso, `/api/whatsapp` devuelve 404.

---

## 0. WhatsApp → HTTP: lo que llega al endpoint

El usuario escribe por WhatsApp → ACS publica un evento en **Event Grid** → Event Grid
hace **POST** a `/api/whatsapp`.

Lo que llega **no es un `Activity`**: es un **array de `EventGridEvent`**. El `Activity`
todavía no existe en ninguna parte; lo fabrica el adapter dos capas más abajo.

```json
[
  {
    "id": "5f5d4c...",
    "topic": "/subscriptions/.../Microsoft.Communication/CommunicationServices/mi-acs",
    "subject": "/phoneNumber/15550001111",
    "eventType": "Microsoft.Communication.AdvancedMessageReceived",
    "eventTime": "2026-09-16T12:00:00Z",
    "dataVersion": "1.0",
    "data": {
      "from": "5491134567890",
      "fromBSUID": "",
      "to": "15550001111",
      "receivedTimestamp": "2026-09-16T12:00:00Z",
      "content": "hola",
      "channelType": "whatsapp",
      "messageId": "wamid.ABC..."
    }
  }
]
```

Los payloads reales, listos para postear con `curl`, están en
[`eventgrid-samples/`](./eventgrid-samples/): `01-validation.json`,
`02-message-telefono.json`, `03-message-bsuid.json`.

El sobre (`EventGridEvent`) es de Event Grid; el `data` de adentro es de ACS. Tres tipos
de evento importan, y el controller los distingue por el tipo .NET del `data`:

| Evento | Tipo .NET del `data` | Qué hace el controller |
|---|---|---|
| Handshake de suscripción | `SubscriptionValidationEventData` | Devuelve el `ValidationCode`. **Sin esto la suscripción nunca se activa** |
| Mensaje entrante | `AcsMessageReceivedEventData` | Lo traduce a `Activity` y corre el turn |
| Estado de entrega | `AcsMessageDeliveryStatusUpdatedEventData` | Solo loguea |

> **Gotcha de paquetes:** esos tipos viven en **`Azure.Messaging.EventGrid.SystemEvents`**,
> que en EventGrid 5.0.0 se separó del paquete principal
> (`WhatsAppToAcsChannelAdapter.csproj:20`). Y el nombre de la clase **no** sigue al del
> evento: el evento se llama `AdvancedMessageReceived` pero la clase es
> `AcsMessageReceivedEventData`. El campo `FromBsuid` (con esa capitalización, aunque el
> JSON diga `fromBSUID`) solo es público desde SystemEvents **1.1.0**.

**No hay JWT que validar.** En Bot Framework la confianza venía de un `Bearer` emitido por
el Bot Connector Service; acá viene de que solo Event Grid conoce la URL de la suscripción
(`AcsWhatsAppAdapter.cs:67-68`).

---

## 1. Endpoint / Controller — `WhatsAppToAcsChannelAdapter/Controllers/EventGridController.cs`

`PostAsync(CancellationToken)` (`:31`) **no tiene `[FromBody]`**: lee el body crudo con
`BinaryData.FromStreamAsync(Request.Body)` (`:33`) y lo parsea él mismo con
`EventGridEvent.ParseMany` (`:39`). El model binding de MVC no participa — igual que en el
`BotController` original, y por el mismo motivo: el parseo lo hace el SDK, no MVC.

Después resuelve el `data` a su tipo concreto con `TryGetSystemEventData` (`:53`) y
despacha con un `switch` por tipo (`:60-85`). Si el SDK no reconoce el evento, el `continue`
(`:56`) saltea **ese** evento del lote y sigue con los demás.

**Lo que el controller manda al adapter** (`:96-97`):

| Objeto | Tipo | De dónde sale |
|---|---|---|
| `activity` | `Microsoft.Bot.Schema.Activity` | `AcsActivityFactory.CreateMessageActivity(mensaje)` |
| `_bot` | `IBot` (acá `BotCharly`) | inyectado (`Charly/Startup.cs:56`) |
| `cancellationToken` | `CancellationToken` | del request |

Diferencia clave con Bot Framework: acá **el controller ya manda un `Activity` armado**.
La traducción evento→`Activity` es una función pura en `AcsActivityFactory`, separada del
adapter justamente para poder testearla sin I/O.

Dos decisiones que parecen raras y son deliberadas:

- **Siempre responde `200`** (`:48`, `:87`), incluso cuando falla. Event Grid **reintenta**
  ante cualquier error, y un reintento significa que el usuario recibe la respuesta del bot
  **duplicada**. Por eso `ProcessMessageAsync` (`:91`) se traga la excepción y solo loguea
  (`:99-105`).
- El `catch` del `ParseMany` (`:41-48`) loguea explícito porque la causa típica —
  suscripción creada con esquema CloudEvents en vez de Event Grid Schema — se manifiesta
  como "no pasa nada".

---

## 2. Adapter — `WhatsAppToAcsChannelAdapter/Adapters/AcsWhatsAppAdapter.cs` (hereda `BotAdapter`)

Es la capa que traduce transporte ↔ modelo de dominio. Hereda de **`BotAdapter`**, no de
`CloudAdapter`: todo lo que `CloudAdapter` hacía con HTTP y JWT sobra acá.

`ProcessActivityAsync(activity, bot, ct)` (`:65`) es el punto de entrada del turn, y son
literalmente cuatro líneas:

```csharp
using (var turnContext = new TurnContext(this, activity))
{
    await RunPipelineAsync(turnContext, bot.OnTurnAsync, cancellationToken);
}
```

1. **Arma el `TurnContext`** a mano (`:69`), envolviendo *adapter + Activity*. Su
   `TurnState` — un `TurnContextStateCollection`, diccionario que vive **solo ese turno** —
   arranca vacío: no hay `IConnectorClient` ni `UserTokenClient` que guardar, porque el
   envío usa el `NotificationMessagesClient` que el adapter ya tiene como campo (`:18`).
2. **Corre el pipeline**: `RunPipelineAsync` (`:71`) pasa por los middlewares del
   `MiddlewareSet` (hoy ninguno registrado) y termina llamando al callback `bot.OnTurnAsync`.
   > La firma es `RunPipelineAsync(ITurnContext, BotCallbackHandler, CancellationToken)`.
   > **No existe** un overload que tome `ClaimsIdentity` + `Activity` — ese era el camino de
   > `CloudAdapter`, no de `BotAdapter`.
3. `OnTurnError` (`:40-61`) envuelve el turn: si el bot tira, loguea y avisa al usuario.
   Con un detalle propio de este canal — si la excepción es `RequestFailedException`
   (`:46`), **corta y no intenta avisar**, porque avisar significaría enviar por el mismo
   canal de ACS que acaba de fallar. Sin ese corte, un envío fallido generaba dos intentos
   en vez de uno.

**Lo que el adapter manda al bot** — dos objetos, y nada más:

| Objeto | Tipo | Contenido |
|---|---|---|
| `turnContext` | `ITurnContext` | `.Activity` (el `Activity` traducido), `.TurnState`, `.Adapter`, `.Responded` |
| `cancellationToken` | `CancellationToken` | corte del request |

**Esta fila es idéntica a la de Bot Framework.** Es el invariante.

### La traducción: `Adapters/AcsActivityFactory.cs`

Funciones puras, sin I/O. `CreateMessageActivity` (`:14`) produce el `Activity`:

| Campo del `Activity` | Valor | Nota |
|---|---|---|
| `Type` | `ActivityTypes.Message` | siempre: en WhatsApp no hay `conversationUpdate` |
| `Id` | `evento.MessageId` | el `wamid.*` de WhatsApp |
| `ChannelId` | `"whatsapp"` | constante (`:10`) |
| `ServiceUrl` | `string.Empty` | **a propósito** (`:27`): no hay Connector API a la que postear |
| `From.Id` | `ResolveIdentity(evento)` | ver abajo |
| `Recipient.Id` | `evento.To` | el número del negocio |
| `Conversation.Id` | el mismo valor que `From.Id` | `:32` — ver abajo |
| `Text` | `ExtractText(evento)` | ver abajo |
| `ChannelData` | el evento ACS completo | `:40` — el bot puede mirar el crudo si necesita |
| `Attachments` | `Attachment` con el `MediaId` en `.Content` | `:97-111` |

**Identidad (`ResolveIdentity`, `:53`)** — el punto que rompe las implementaciones
ingenuas. Desde que WhatsApp introdujo los *usernames*, el campo `from` (el teléfono)
**puede venir vacío** si el usuario lo ocultó, y la identidad real viene en el **BSUID**
(business-scoped user ID). La regla es `FromBsuid ?? From`, en ese orden (`:55-62`), y si
no hay ninguno de los dos tira excepción en vez de seguir con un id vacío (`:65`). El
sample `03-message-bsuid.json` es exactamente ese caso.

**`Conversation.Id` es la identidad del usuario, y es estable de por vida** (`:32`): en
WhatsApp no existe el concepto de "sesión". Consecuencia directa: el estado conversacional
que se guarde con esa clave **persiste para siempre**, así que `MemoryStorage` no sirve ni
para desarrollo serio.

**`ExtractText` (`:68`)** normaliza todo a texto: un toque en un botón interactivo
(`ButtonReply`/`ListReply`, `:73-81`) o en un botón de plantilla (`Button.Payload`, `:85`)
llega al bot como si el usuario lo hubiera tipeado. Así el `switch` del bot no necesita
saber si vino de un botón o del teclado.

> Efecto colateral de que *todo* colapse a un `message` con `Text`: una **reacción** (emoji)
> o una **foto sin caption** producen un `Activity` con `Text` vacío, y el bot cae en el
> `default` de su `switch`. Hoy eso no está filtrado.

**Adjuntos (`CreateAttachment`, `:97`)**: ACS no entrega una URL pública del media, entrega
un `MediaId` que hay que bajar con `NotificationMessagesClient.DownloadMedia`. Ese id queda
en `Attachment.Content` (`:109`) para que el bot decida si lo descarga.

---

## 3. Bot — `Charly/Bots/BotCharly.cs` (`ActivityHandler`, implementa `IBot`)

**Esta capa no cambió con la migración.** Ni una línea de lógica; solo se movió de proyecto
y cambió de namespace a `Charly.Bots`.

`IBot` tiene un único método:

```csharp
Task OnTurnAsync(ITurnContext turnContext, CancellationToken cancellationToken);
```

Devuelve `Task`: **el bot no retorna la respuesta**. Cuando quiere contestar, la manda él
mismo (capa 4).

`ActivityHandler` implementa ese método como un **router por `Activity.Type`**, y para cada
rama re-tipa el contexto con `DelegatingTurnContext<T>`:

| `Activity.Type` | Método virtual | Objetos que recibís |
|---|---|---|
| `message` | `OnMessageActivityAsync` | `ITurnContext<IMessageActivity>` |
| `conversationUpdate` | `OnConversationUpdateActivityAsync` → `OnMembersAddedAsync` / `OnMembersRemovedAsync` | `IList<ChannelAccount>` + `ITurnContext<IConversationUpdateActivity>` |
| `event` | `OnEventActivityAsync` | `ITurnContext<IEventActivity>` |
| `invoke` | `OnInvokeActivityAsync` (devuelve `InvokeResponse`) | `ITurnContext<IInvokeActivity>` |
| `messageReaction`, `installationUpdate`, `endOfConversation`… | sus `OnXxxAsync` | contexto tipado |
| cualquier otro | `OnUnrecognizedActivityTypeAsync` | `ITurnContext<IActivity>` |

Ese re-tipado es **solo conveniencia de compilación**: por debajo sigue siendo el mismo
objeto `Activity`, accesible completo en `turnContext.Activity`. `IMessageActivity` te da
`Text` sin castear; `IConversationUpdateActivity` te da `MembersAdded`.

En este repo se sobreescriben dos:

- `OnMessageActivityAsync` (`:32`) — el método central, con el `switch` de comandos. Es el
  único que se ejecuta hoy.
- `OnMembersAddedAsync` (`:15`) — **código muerto en WhatsApp**. El adapter solo produce
  activities de tipo `message`; no existe `conversationUpdate`, así que el saludo de
  bienvenida nunca se dispara. Si se quiere un onboarding, hay que detectar el primer
  mensaje del usuario y responder distinto.

El bot es **`Transient`** (`Charly/Startup.cs:56`): una instancia nueva por turn. Por eso el
estado conversacional no puede vivir en campos de la clase.

---

## 4. Bot → adapter → WhatsApp: lo que sale

1. `MessageFactory.Text("...")` devuelve un `IMessageActivity` — un `Activity` con
   `Type = "message"`, `Text`, `Speak`, `InputHint`. Es un objeto **mutable**: por eso en
   `BotCharly.cs:64-75` se le puede colgar `SuggestedActions` con una `List<CardAction>`.
2. `TurnContext.SendActivitiesAsync(Activity[])` completa el **sobre** de la respuesta a
   partir del activity entrante: invierte `From`/`Recipient`, copia `Conversation` y
   `ServiceUrl`, setea `ReplyToId`. Vos solo pusiste el `Text`.
3. Corre los handlers `OnSendActivities` de los middlewares registrados.
4. Llama `Adapter.SendActivitiesAsync(turnContext, activities, ct)`
   (`AcsWhatsAppAdapter.cs:76`), que es donde termina la abstracción:

   | Qué mandó el bot | Qué envía el adapter |
   |---|---|
   | `Type != "message"` (p. ej. `typing`) | nada — se ignora en silencio (`:84-89`) |
   | `Text` a secas | `TextNotificationContent` (`:162`) |
   | `Attachments` con `ContentUrl` `image/*` | `ImageNotificationContent` (`:176`) |
   | …`video/*` | `VideoNotificationContent` (`:180`) |
   | …`audio/*` | `AudioNotificationContent` (`:184`) |
   | …cualquier otro | `DocumentNotificationContent` (`:188`) |

   El destinatario es `activity.Conversation.Id` (`:91`) — el mismo valor que salió de
   `ResolveIdentity`, que sirve indistintamente si es un E.164 o un BSUID.
5. `NotificationMessagesClient.SendAsync(contenido, ct)` → `Response<SendMessageResult>`.
   El adapter extrae `Receipts.First().MessageId` (`FirstMessageId`, `:211-214`) y lo
   devuelve envuelto en un **`ResourceResponse[]`** (`:113`), que es el valor de retorno de
   `SendActivityAsync`.

**La respuesta nunca vuelve por el HTTP entrante.** Sale por una llamada HTTPS distinta,
del proceso a ACS. El POST de Event Grid ya contestó `200` con body vacío.

Tres límites del canal que el adapter maneja explícitamente:

- **`SuggestedActions` no se traduce solo.** WhatsApp tiene interactivos nativos con límites
  duros (3 botones de respuesta rápida, o lista de hasta 10 ítems) que hay que emitir
  explícitamente — y el SDK estable `Azure.Communication.Messages 1.1.0` **todavía no los
  soporta** (aparecen en 1.2.0-beta.1). Por ahora se **degradan a texto numerado**
  (`FlattenSuggestedActions`, `:199-209`): el `case "opciones"` de `BotCharly.cs` sigue
  funcionando, pero como lista de texto en vez de botones.
- **Ventana de 24 h.** Fuera de las 24 h desde el último mensaje del usuario, WhatsApp solo
  acepta plantillas preaprobadas. El adapter no puede arreglarlo, pero **falla legible**: el
  `catch (RequestFailedException)` (`:102-111`) pasa el status por `DiagnoseSendFailure`
  (`:118-152`), que traduce el código HTTP a la causa probable — 400 pedido inválido, 401
  connection string, 403 permisos, 404 channel id, 429 rate limit, 470 ventana cerrada, 5xx
  transitorio de ACS.
- **No se puede editar ni borrar.** `UpdateActivityAsync` (`:219`) y `DeleteActivityAsync`
  (`:222`) tiran `NotSupportedException`. Están implementados solo porque `BotAdapter` los
  declara `abstract`.

---

## Resumen: una línea por frontera

```
WhatsApp/ACS   --EventGridEvent[] (JSON)-->          POST /api/whatsapp
controller     --Activity, IBot, CancellationToken-->adapter.ProcessActivityAsync
adapter        --ITurnContext (con .Activity) + CT-->bot.OnTurnAsync
ActivityHandler--ITurnContext<IMessageActivity>-->   OnMessageActivityAsync
bot            --IActivity (MessageFactory.Text)-->  turnContext.SendActivityAsync
adapter        --NotificationContent (SDK ACS)-->    ACS  (=> ResourceResponse[])
```

---

## Apéndice A: cómo era con Bot Framework, y qué sobrevivió

Vale conocerlo: es el 90 % de la documentación y los samples de Bot Framework que vas a
encontrar, y es el contraste que hace visible el invariante.

El proyecto arrancó con la plantilla **EmptyBot v4.22.0**, que traía
`Controllers/BotController.cs` y `AdapterWithErrorHandler.cs : CloudAdapter`
(ambos eliminados en la migración):

- **Llegaba al endpoint** `/api/messages` un **`Activity` en JSON** directo, más un header
  `Authorization: Bearer <JWT>` del Bot Connector Service.
- **El controller mandaba al adapter** `HttpRequest`, `HttpResponse` e `IBot` — la request
  **cruda**, sin deserializar, porque el adapter necesitaba el body para su propio
  Newtonsoft, el header para validar el JWT, y la request completa para poder hacer upgrade
  a WebSocket.
- **El adapter** (`CloudAdapter.ProcessAsync`) deserializaba a `Activity`, validaba el JWT
  → `ClaimsIdentity`, llenaba el `TurnState` con `IConnectorClient`, `UserTokenClient` y
  `ConnectorFactory`, y corría el pipeline.
- **La salida** iba por `IConnectorClient.Conversations.ReplyToActivityAsync` — un **POST
  saliente al `activity.ServiceUrl`** que había indicado el canal.

Comparación frontera por frontera:

| Frontera | Bot Framework (antes) | ACS/WhatsApp (hoy) |
|---|---|---|
| Llega al endpoint | `Activity` JSON en `/api/messages` | `EventGridEvent[]` en `/api/whatsapp` |
| Autenticación | `Bearer` JWT validado por el adapter | la suscripción de Event Grid; sin JWT |
| Controller → adapter | `HttpRequest`, `HttpResponse`, `IBot` | `Activity`, `IBot`, `CancellationToken` |
| Quién deserializa | el adapter | el controller (`ParseMany`) |
| **Adapter → bot** | `ITurnContext` + `CancellationToken` | **idéntico** |
| **Bot → adapter** | `IActivity` | **idéntico** |
| Adapter → canal | `IConnectorClient`, POST al `ServiceUrl` | `NotificationMessagesClient.SendAsync` |
| Identidad | la ponía el canal en `From.Id` | `FromBsuid ?? From` |
| Tipos de activity | todos los del esquema | solo `message` |

Las dos filas en negrita son el invariante, y son exactamente la razón por la que la lógica
del bot sobrevivió sin un solo cambio. Si hubiera tocado el `HttpRequest` o el
`IConnectorClient` directamente, cambiar de canal habría significado reescribirla.

Tres cosas de la documentación de Bot Framework que **ya no aplican** acá, y conviene tener
presentes para no perder tiempo:

- **`deliveryMode: "expectReplies"`** (pedir que las respuestas vuelvan en el body del POST
  entrante) era una función del `CloudAdapter`. No existe en este adapter.
- **El Bot Framework Emulator** ya no sirve como herramienta de desarrollo: no hay
  `/api/messages`. El reemplazo es postear los JSON de
  [`eventgrid-samples/`](./eventgrid-samples/) con `curl` a `/api/whatsapp`.
- Los **mensajes proactivos** siguen necesitando guardar la `ConversationReference` y usar
  `ContinueConversationAsync`, pero además chocan con la ventana de 24 h: fuera de ella
  requieren `TemplateNotificationContent`.
