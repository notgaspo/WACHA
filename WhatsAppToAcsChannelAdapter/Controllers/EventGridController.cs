using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using WhatsAppToAcsChannelAdapter.Adapters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Bot.Builder;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace WhatsAppToAcsChannelAdapter.Controllers
{
    [Route("api/whatsapp")]
    [ApiController]
    public class EventGridController : ControllerBase
    {
        private readonly AcsWhatsAppAdapter _adapter; //crea el WhatsappAdapter
        private readonly IBot _bot; //crea la interfaz de bot vacia para llenar con el bot que quieras
        private readonly ILogger<EventGridController> _logger; //esto es un logger NI IDEA

        //clase constructora, inyecta las dependencias
        public EventGridController(AcsWhatsAppAdapter adapter, IBot bot, ILogger<EventGridController> logger)
        {
            _adapter = adapter;
            _bot = bot;
            _logger = logger;
        }

        [HttpPost]
        //posttAsync se ejecuta cada vez que llega un post, traduzco y chequeo todo, y se lo mando a ProcesarMensaje
        public async Task<IActionResult> PostAsync(CancellationToken cancellationToken)
        {
            var body = await BinaryData.FromStreamAsync(Request.Body, cancellationToken); //saca el json del paquete

            EventGridEvent[] eventos;
            //aca intento transofmrar el body a una lista de eventos con formato ACS, si no sale te dice que el formato ta mal
            try
            {
                eventos = EventGridEvent.ParseMany(body); //deserealiza el json del body
            }
            catch (Exception ex)
            {
                // Causa tipica: la suscripcion se creo con esquema CloudEvents en vez de
                // Event Grid. Se loguea explicito porque si no el sintoma es "no pasa nada".
                _logger.LogError(ex,
                    "No se pudo parsear el body como Event Grid. Revisa el esquema de entrega " +
                    "de la suscripcion (debe ser Event Grid Schema).");
                return Ok();
            }

            foreach (var evento in eventos)
            {
                if (!evento.TryGetSystemEventData(out var data))
                {
                    _logger.LogWarning("Evento no reconocido: {EventType}", evento.EventType);
                    continue;
                }

                switch (data)
                {
                   
                    case SubscriptionValidationEventData validacion:
                        _logger.LogInformation("Handshake de validacion de Event Grid recibido.");
                        return Ok(new SubscriptionValidationResponse
                        {
                            ValidationResponse = validacion.ValidationCode,
                        }); //esste es el handshake de asure

                    case AcsMessageReceivedEventData mensaje:
                        await ProcessMessageAsync(mensaje, cancellationToken); //AHORA SI RAAAHHHH PRENDAN LA FABRICA LETS GOOO MENSAJE RECIBIDO SEEEE
                        break; //evento de tipo ReceivedEventData, que como usamos whatsapp esto es 99% un mensaje

                    case AcsMessageDeliveryStatusUpdatedEventData estado:
                        _logger.LogInformation(
                            "Estado de entrega del mensaje {MessageId}: {Status}",
                            estado.MessageId, estado.Status);
                        break; //estado del mensaje, cambiamos de enviado a recibido, a fallado o a lo q sea.

                    default:
                        _logger.LogDebug("Evento ignorado: {EventType}", evento.EventType);
                        break;//llega hasta aca si el evento coincide con algun tipo estandar de SDK pero no con uno de Asure
                }
            }

            // Siempre 200. Event Grid reintenta ante cualquier error, y un reintento
            // significa que el usuario recibe la respuesta del bot duplicada.
            return Ok();
        }

        //TOMO EL MENSAJE SEEE PROCESAR MENSAJES ME ENCANTA DENME MAS MENSAJES O SI SOY PROCESARMENSAJEASYNC FUCK YEAAAAA
        private async Task ProcessMessageAsync(
            AcsMessageReceivedEventData mensaje, CancellationToken cancellationToken) 
        {
            try
            {
                var activity = AcsActivityFactory.CreateMessageActivity(mensaje); //PRENDO LA FABRICAAA YEAAAHH
                await _adapter.ProcessActivityAsync(activity, _bot, cancellationToken); //le paso el activity al adapter de whatsapp.
            }
            catch (Exception ex)
            {
                // Se traga la excepcion a proposito: ver el comentario del 200 de arriba.
                _logger.LogError(ex,
                    "Error procesando el mensaje {MessageId}: {Message}",
                    mensaje.MessageId, ex.Message);
            }
        }
    }
}
