# CAPITULO 1: OBJETOS.

Referencia de los objetos que circulan por el bot. Cada sección lista **todos** los
miembros del objeto, con el comentario de qué hace y si aplica a este proyecto.

Listas verificadas contra los XML de documentación de `Microsoft.Bot.Builder` y
`Microsoft.Bot.Schema` **4.22.0**, no de memoria.

| Marca | Significado en este proyecto (WhatsApp vía ACS) |
|---|---|
| ✅ | Lo usás / viene poblado |
| ⚠️ | Existe pero con una salvedad importante |
| ❌ | Nulo o ignorado: es de otro canal |

---

## TurnContext

`ITurnContext` — lo que el adapter le pasa al bot. Cuatro propiedades y nueve métodos:
**datos + capacidad de contestar**. Es `IDisposable`.

```csharp
// ── PROPIEDADES ──
Activity                    Activity     // ✅ el Activity entrante. Solo lectura
BotAdapter                  Adapter      // ⚠️ solo si necesitás ContinueConversationAsync
TurnContextStateCollection  TurnState    // ⚠️ diccionario que vive SOLO este turno. Hoy vacío
bool                        Responded    // ✅ ¿ya mandaste algo en este turno?

// ── MANDAR RESPUESTA ──
Task<ResourceResponse>   SendActivityAsync(string text, string speak, string inputHint, CT)
                                         // ✅ el atajo. speak/inputHint se ignoran en WhatsApp
Task<ResourceResponse>   SendActivityAsync(IActivity activity, CT)
                                         // ✅ para adjuntos o SuggestedActions
Task<ResourceResponse[]>  SendActivitiesAsync(IActivity[] activities, CT)
                                         // ✅ varios mensajes en orden

// ── MODIFICAR LO YA ENVIADO ──
Task<ResourceResponse>   UpdateActivityAsync(IActivity activity, CT)
                                         // ❌ NotSupportedException: WhatsApp no permite editar
Task                     DeleteActivityAsync(string activityId, CT)
                                         // ❌ NotSupportedException: no permite borrar
Task                     DeleteActivityAsync(ConversationReference reference, CT)
                                         // ❌ idem

// ── GANCHOS DE MIDDLEWARE ──
ITurnContext OnSendActivities(SendActivitiesHandler handler)
                                         // ⚠️ interceptar antes de enviar. Sin middleware hoy
ITurnContext OnUpdateActivity(UpdateActivityHandler handler)   // ⚠️ inútil: update no se soporta
ITurnContext OnDeleteActivity(DeleteActivityHandler handler)   // ⚠️ inútil: delete no se soporta
```

La variante genérica solo **angosta el tipo de `.Activity`**, no agrega miembros:

```csharp
ITurnContext<T> : ITurnContext  where T : IActivity
    new T Activity                       // ✅ ITurnContext<IMessageActivity> → .Activity.Text sin castear
```

> En la práctica el 95 % del código del bot son dos miembros:
> `turnContext.Activity.Text` para leer y `turnContext.SendActivityAsync(...)` para contestar.

---

## Activity

`Microsoft.Bot.Schema.Activity` — un DTO de **43 propiedades**, sin comportamiento. Se
llenan según el `Type`; en cualquier mensaje concreto la mayoría es `null`.

```csharp
// ── SOBRE: quién, cuándo, por dónde contestar ──
string              Type             // ✅ rutea al método del handler. Acá siempre "message"
string              Id               // ✅ id en el canal. En WhatsApp: "wamid.ABC..."
DateTimeOffset?     Timestamp        // ✅ UTC de creación (evento.ReceivedTimestamp)
DateTimeOffset?     LocalTimestamp   // ❌ hora local del emisor con offset
string              LocalTimezone    // ❌ zona horaria IANA del emisor
string              CallerId         // ❌ quién invocó al bot (skills / BF auth)
string              ServiceUrl       // ⚠️ a dónde postear la respuesta. Vacío A PROPÓSITO: no hay Connector API
string              ChannelId        // ✅ "whatsapp"
ChannelAccount      From             // ✅ emisor. Id = BSUID ?? E.164. Name viene null
ChannelAccount      Recipient        // ✅ receptor. Id = tu número de negocio
ConversationAccount Conversation     // ✅ Id = From.Id, y es ESTABLE DE POR VIDA (no hay "sesión")

// ── CONTENIDO DEL MENSAJE ──
string              Text             // ✅ el texto. También el Title del botón que tocaron
string              TextFormat       // ✅ "plain" | "markdown" | "xml". Acá "plain"
string              Locale           // ❌ idioma del emisor. WhatsApp no lo manda
string              Speak            // ❌ SSML para voz. Se ignora al enviar
string              InputHint        // ❌ "acceptingInput" | "expectingInput" | "ignoringInput"
string              Summary          // ❌ resumen corto para notificaciones
List<Attachment>    Attachments      // ⚠️ ENTRA con MediaId en .Content y sin ContentUrl;
                                     //    SALE exigiendo .ContentUrl. No son intercambiables
string              AttachmentLayout // ❌ "list" | "carousel". Ignorado al enviar
SuggestedActions    SuggestedActions // ⚠️ solo se usa .Actions[].Title, aplanado a texto numerado
List<Entity>        Entities         // ❌ menciones y entidades que reconoció el canal
object              ChannelData      // ✅ el AcsMessageReceivedEventData completo. Tu escotilla

// ── SEGÚN EL TYPE (ninguno llega por WhatsApp) ──
IList<ChannelAccount>  MembersAdded     // ❌ conversationUpdate: quiénes entraron
IList<ChannelAccount>  MembersRemoved   // ❌ conversationUpdate: quiénes salieron
IList<MessageReaction> ReactionsAdded   // ❌ messageReaction. En WhatsApp la reacción viene como message
IList<MessageReaction> ReactionsRemoved // ❌ idem
string                 Name             // ❌ event/invoke: "tokens/response", "adaptiveCard/action"
object                 Value            // ❌ event/invoke: el payload
string                 ValueType        // ❌ tipo del Value
string                 Action           // ❌ installationUpdate: "add" | "remove"
string                 Code             // ❌ endOfConversation: motivo del cierre
string                 TopicName        // ❌ nombre del tema de la conversación
bool?                  HistoryDisclosed // ❌ si se reveló el historial

// ── RUTEO Y METADATOS ──
string               ReplyToId       // ❌ a qué mensaje responde. El TurnContext lo setea, ACS no lo usa
ConversationReference RelatesTo      // ❌ referencia a otra conversación
string               Label           // ❌ etiqueta de trace activities
string               Importance      // ❌ "low" | "normal" | "high"
string               DeliveryMode    // ❌ "normal" | "notification" | "expectReplies".
                                     //    expectReplies era del CloudAdapter: no existe acá
DateTimeOffset?      Expiration      // ❌ hasta cuándo tiene sentido entregar el mensaje
string[]             ListenFor       // ❌ hints de speech priming
IList<TextHighlight> TextHighlights  // ❌ fragmentos a resaltar en el mensaje citado
SemanticAction       SemanticAction  // ❌ acción programática (skills)

// ── ESCOTILLA DE SERIALIZACIÓN ──
JObject              Properties      // ⚠️ [JsonExtensionData]: campos del JSON que no mapean
                                     //    a ninguna propiedad. Rara vez lo necesitás
```

### Lo que realmente tenés en este proyecto

**Entra** (la factory setea 11, el resto es `null`):
`Type` · `Id` · `Timestamp` · `ChannelId` · `ServiceUrl` · `From` · `Recipient` ·
`Conversation` · `Text` · `TextFormat` · `ChannelData` · (`Attachments` si vino media)

**Sale** (el adapter lee 4 y descarta el resto):
`Type` (si no es `message` se descarta en silencio) · `Conversation.Id` (destinatario) ·
`Text` · `Attachments[0].ContentUrl`+`.ContentType` · `SuggestedActions.Actions[].Title`

> `Activity` es **datos**; `TurnContext` es **capacidad**. El Activity te dice qué pasó;
> el TurnContext es lo que te permite hacer algo al respecto.
