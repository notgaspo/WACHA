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
el adapter, y **`EmptyBot.cs` no cambió ni una línea**. El apéndice A cuenta cómo era y
qué sobrevivió exactamente.

## Estructura

Una carpeta por capa, en el orden en que se atraviesan:

```
Charly/
  Controllers/EventGridController.cs   endpoint HTTP: recibe eventos de Event Grid
  Adapters/AcsWhatsAppAdapter.cs       transporte ↔ dominio: corre el turn y envía a ACS
  Adapters/AcsActivityFactory.cs       traducción pura evento ACS → Activity
  Bots/EmptyBot.cs                     la lógica del bot; no conoce el transporte
  Config/AcsOptions.cs                 opciones tipadas (connection string, channel id)
  Startup.cs, Program.cs               cableado de DI y host
```

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
[`docs/eventgrid-samples/`](../../docs/eventgrid-samples/) — en la **raíz del repo**, no en
esta carpeta: `01-validation.json`, `02-message-telefono.json`, `03-message-bsuid.json`.

El sobre (`EventGridEvent`) es de Event Grid; el `data` de adentro es de ACS. Tres tipos
de evento importan, y el controller los distingue por el tipo .NET del `data`:

| Evento | Tipo .NET del `data` | Qué hace el controller |
|---|---|---|
| Handshake de suscripción | `SubscriptionValidationEventData` | Devuelve el `ValidationCode`. **Sin esto la suscripción nunca se activa** |
| Mensaje entrante | `AcsMessageReceivedEventData` | Lo traduce a `Activity` y corre el turn |
| Estado de entrega | `AcsMessageDeliveryStatusUpdatedEventData` | Solo loguea |

> **Gotcha de paquetes:** esos tipos viven en **`Azure.Messaging.EventGrid.SystemEvents`**,
> que en EventGrid 5.0.0 se separó del paquete principal (`Charly.csproj:12`). Y el nombre
> de la clase **no** sigue al del evento: el evento se llama `AdvancedMessageReceived` pero
> la clase es `AcsMessageReceivedEventData`. El campo `FromBsuid` (con esa
> capitalización, aunque el JSON diga `fromBSUID`) solo es público desde SystemEvents
> **1.1.0**.

**No hay JWT que validar.** En Bot Framework la confianza venía de un `Bearer` emitido por
el Bot Connector Service; acá viene de que solo Event Grid conoce la URL de la suscripción
(`AcsWhatsAppAdapter.cs:80-81`).

---

## 1. Endpoint / Controller — `Controllers/EventGridController.cs`

`PostAsync(CancellationToken)` **no tiene `[FromBody]`**: lee el body crudo con
`BinaryData.FromStreamAsync(Request.Body)` (`:35`) y lo parsea él mismo con
`EventGridEvent.ParseMany` (`:40`). El model binding de MVC no participa — igual que en el
`BotController` original, y por el mismo motivo: el parseo lo hace el SDK, no MVC.

Después resuelve el `data` a su tipo concreto con `TryGetSystemEventData` (`:54`) y
despacha con un `switch` por tipo (`:60-84`).

**Lo que el controller manda al adapter** (`:97-98`):

| Objeto | Tipo | De dónde sale |
|---|---|---|
| `activity` | `Microsoft.Bot.Schema.Activity` | `AcsActivityFactory.CrearMessageActivity(mensaje)` |
| `_bot` | `IBot` (acá `EmptyBot`) | inyectado (`Startup.cs:52`) |
| `cancellationToken` | `CancellationToken` | del request |

Diferencia clave con Bot Framework: acá **el controller ya manda un `Activity` armado**.
La traducción evento→`Activity` es una función pura en `AcsActivityFactory`, separada del
adapter justamente para poder testearla sin I/O.

Dos decisiones que parecen raras y son deliberadas:

- **Siempre responde `200`** (`:49`, `:89`), incluso cuando falla. Event Grid **reintenta**
  ante cualquier error, y un reintento significa que el usuario recibe la respuesta del bot
  **duplicada**. Por eso `ProcesarMensajeAsync` se traga la excepción y solo loguea
  (`:100-106`).
- El `catch` del `ParseMany` (`:42-50`) loguea explícito porque la causa típica —
  suscripción creada con esquema CloudEvents en vez de Event Grid Schema — se manifiesta
  como "no pasa nada".

---

## 2. Adapter — `Adapters/AcsWhatsAppAdapter.cs` (hereda `BotAdapter`)

Es la capa que traduce transporte ↔ modelo de dominio. Hereda de **`BotAdapter`**, no de
`CloudAdapter`: todo lo que `CloudAdapter` hacía con HTTP y JWT sobra acá.

`ProcessActivityAsync(activity, bot, ct)` (`:78`) es el punto de entrada del turn, y son
literalmente cuatro líneas:

```csharp
using (var turnContext = new TurnContext(this, activity))
{
    await RunPipelineAsync(turnContext, bot.OnTurnAsync, cancellationToken);
}
```

1. **Arma el `TurnContext`** a mano (`:82`), envolviendo *adapter + Activity*. Su
   `TurnState` — un `TurnContextStateCollection`, diccionario que vive **solo ese turno** —
   arranca vacío: no hay `IConnectorClient` ni `UserTokenClient` que guardar, porque el
   envío usa el `NotificationMessagesClient` que el adapter ya tiene como campo (`:28`).
2. **Corre el pipeline**: `RunPipelineAsync` (`:84`) pasa por los middlewares del
   `MiddlewareSet` (hoy ninguno registrado) y termina llamando al callback `bot.OnTurnAsync`.
   > La firma es `RunPipelineAsync(ITurnContext, BotCallbackHandler, CancellationToken)`.
   > **No existe** un overload que tome `ClaimsIdentity` + `Activity` — ese era el camino de
   > `CloudAdapter`, no de `BotAdapter`.
3. `OnTurnError` (`:50-71`) envuelve el turn: si el bot tira, loguea y avisa al usuario.
   Con un detalle propio de este canal — si la excepción es `RequestFailedException`
   (`:56`), **corta y no intenta avisar**, porque avisar significaría enviar por el mismo
   canal de ACS que acaba de fallar. Sin ese corte, un envío fallido generaba dos intentos
   en vez de uno.

**Lo que el adapter manda al bot** — dos objetos, y nada más:

| Objeto | Tipo | Contenido |
|---|---|---|
| `turnContext` | `ITurnContext` | `.Activity` (el `Activity` traducido), `.TurnState`, `.Adapter`, `.Responded` |
| `cancellationToken` | `CancellationToken` | corte del request |

**Esta fila es idéntica a la de Bot Framework.** Es el invariante.

### La traducción: `Adapters/AcsActivityFactory.cs`

Funciones puras, sin I/O. `CrearMessageActivity` (`:42`) produce el `Activity`:

| Campo del `Activity` | Valor | Nota |
|---|---|---|
| `Type` | `ActivityTypes.Message` | siempre: en WhatsApp no hay `conversationUpdate` |
| `Id` | `evento.MessageId` | el `wamid.*` de WhatsApp |
| `ChannelId` | `"whatsapp"` | constante (`:13`) |
| `ServiceUrl` | `string.Empty` | **a propósito** (`:55`): no hay Connector API a la que postear |
| `From.Id` | `ResolverIdentidad(evento)` | ver abajo |
| `Recipient.Id` | `evento.To` | el número del negocio |
| `Conversation.Id` | el mismo valor que `From.Id` | ver abajo |
| `Text` | `ExtraerTexto(evento)` | ver abajo |
| `ChannelData` | el evento ACS completo | `:70` — el bot puede mirar el crudo si necesita |
| `Attachments` | `Attachment` con el `MediaId` en `.Content` | `:121-135` |

**Identidad (`ResolverIdentidad`, `:26`)** — el punto que rompe las implementaciones
ingenuas. Desde que WhatsApp introdujo los *usernames*, el campo `from` (el teléfono)
**puede venir vacío** si el usuario lo ocultó, y la identidad real viene en el **BSUID**
(business-scoped user ID). La regla es `FromBsuid ?? From`, en ese orden (`:28-36`), y si
no hay ninguno de los dos tira excepción en vez de seguir con un id vacío (`:38`). El
sample `03-message-bsuid.json` es exactamente ese caso.

**`Conversation.Id` es la identidad del usuario, y es estable de por vida** (`:63`): en
WhatsApp no existe el concepto de "sesión". Consecuencia directa: el estado conversacional
que se guarde con esa clave **persiste para siempre**, así que `MemoryStorage` no sirve ni
para desarrollo serio.

**`ExtraerTexto` (`:86`)** normaliza todo a texto: un toque en un botón interactivo
(`ButtonReply`/`ListReply`, `:91-99`) o en un botón de plantilla (`Button.Payload`, `:103`)
llega al bot como si el usuario lo hubiera tipeado. Así el `switch` del bot no necesita
saber si vino de un botón o del teclado.

**Adjuntos (`CrearAdjunto`, `:121`)**: ACS no entrega una URL pública del media, entrega un
`MediaId` que hay que bajar con `NotificationMessagesClient.DownloadMedia`. Ese id queda en
`Attachment.Content` para que el bot decida si lo descarga.

---

## 3. Bot — `Bots/EmptyBot.cs` (`ActivityHandler`, implementa `IBot`)

**Esta capa no cambió con la migración.** Ni una línea de lógica; solo se movió de carpeta
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

- `OnMessageActivityAsync` (`Bots/EmptyBot.cs:32`) — el método central, con el `switch` de
  comandos. Es el único que se ejecuta hoy.
- `OnMembersAddedAsync` (`Bots/EmptyBot.cs:15`) — **código muerto en WhatsApp**. El adapter
  solo produce activities de tipo `message`; no existe `conversationUpdate`, así que el
  saludo de bienvenida nunca se dispara. Si se quiere un onboarding, hay que detectar el
  primer mensaje del usuario y responder distinto.

El bot es **`Transient`** (`Startup.cs:52`): una instancia nueva por turn. Por eso el
estado conversacional no puede vivir en campos de la clase.

---

## 4. Bot → adapter → WhatsApp: lo que sale

1. `MessageFactory.Text("...")` devuelve un `IMessageActivity` — un `Activity` con
   `Type = "message"`, `Text`, `Speak`, `InputHint`. Es un objeto **mutable**: por eso en
   `Bots/EmptyBot.cs:67-75` se le puede colgar `SuggestedActions` con una
   `List<CardAction>`.
2. `TurnContext.SendActivitiesAsync(Activity[])` completa el **sobre** de la respuesta a
   partir del activity entrante: invierte `From`/`Recipient`, copia `Conversation` y
   `ServiceUrl`, setea `ReplyToId`. Vos solo pusiste el `Text`.
3. Corre los handlers `OnSendActivities` de los middlewares registrados.
4. Llama `Adapter.SendActivitiesAsync(turnContext, activities, ct)`
   (`AcsWhatsAppAdapter.cs:88`), que es donde termina la abstracción:

   | Qué mandó el bot | Qué envía el adapter |
   |---|---|
   | `Type != "message"` (p. ej. `typing`) | nada — se ignora en silencio (`:96-101`) |
   | `Text` a secas | `TextNotificationContent` (`:137`) |
   | `Attachments` con `ContentUrl` `image/*` | `ImageNotificationContent` (`:151`) |
   | …`video/*` | `VideoNotificationContent` (`:155`) |
   | …`audio/*` | `AudioNotificationContent` (`:159`) |
   | …cualquier otro | `DocumentNotificationContent` (`:163`) |

   El destinatario es `activity.Conversation.Id` (`:103`) — el mismo valor que salió de
   `ResolverIdentidad`, que sirve indistintamente si es un E.164 o un BSUID.
5. `NotificationMessagesClient.SendAsync(contenido, ct)` (`:138`) → `Response<SendMessageResult>`.
   El adapter extrae `Receipts.First().MessageId` (`:186`) y lo devuelve envuelto en un
   **`ResourceResponse[]`** (`:112`), que es el valor de retorno de `SendActivityAsync`.

**La respuesta nunca vuelve por el HTTP entrante.** Sale por una llamada HTTPS distinta,
del proceso a ACS. El POST de Event Grid ya contestó `200` con body vacío.

Tres límites del canal que el adapter maneja explícitamente:

- **`SuggestedActions` no se traduce solo.** WhatsApp tiene interactivos nativos con límites
  duros (3 botones de respuesta rápida, o lista de hasta 10 ítems) que hay que emitir
  explícitamente. Por ahora se **degradan a texto numerado** (`AplanarSuggestedActions`,
  `:174-184`): el `case "opciones"` de `EmptyBot.cs` sigue funcionando, pero como lista de
  texto en vez de botones.
- **Ventana de 24 h.** Fuera de las 24 h desde el último mensaje del usuario, WhatsApp solo
  acepta plantillas preaprobadas. El adapter no puede arreglarlo, pero **falla legible**:
  el `catch (RequestFailedException)` (`:114-123`) loguea el status de ACS y nombra la
  ventana como causa probable en vez de tragarse el error.
- **No se puede editar ni borrar.** `UpdateActivityAsync` y `DeleteActivityAsync` tiran
  `NotSupportedException` (`:192-198`).

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

Las dos filas en negrita son el invariante, y son exactamente la razón por la que
`EmptyBot.cs` sobrevivió sin un solo cambio. Si la lógica del bot hubiera tocado el
`HttpRequest` o el `IConnectorClient` directamente, cambiar de canal habría significado
reescribirla.

Tres cosas de la documentación de Bot Framework que **ya no aplican** acá, y conviene tener
presentes para no perder tiempo:

- **`deliveryMode: "expectReplies"`** (pedir que las respuestas vuelvan en el body del POST
  entrante) era una función del `CloudAdapter`. No existe en este adapter.
- **El Bot Framework Emulator** ya no sirve como herramienta de desarrollo: no hay
  `/api/messages`. El reemplazo es postear los JSON de
  [`docs/eventgrid-samples/`](../../docs/eventgrid-samples/) con `curl` a `/api/whatsapp`.
- Los **mensajes proactivos** siguen necesitando guardar la `ConversationReference` y usar
  `ContinueConversationAsync`, pero además chocan con la ventana de 24 h: fuera de ella
  requieren `TemplateNotificationContent`.
