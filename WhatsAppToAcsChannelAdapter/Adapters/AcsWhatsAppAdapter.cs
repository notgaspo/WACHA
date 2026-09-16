using Azure;
using Azure.Communication.Messages;
using WhatsAppToAcsChannelAdapter.Config;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WhatsAppToAcsChannelAdapter.Adapters
{
    public class AcsWhatsAppAdapter : BotAdapter
    {
        private readonly NotificationMessagesClient _messagesClient;
        private readonly Guid _channelRegistrationId;
        private readonly ILogger<AcsWhatsAppAdapter> _logger;

        public AcsWhatsAppAdapter(
            NotificationMessagesClient messagesClient,
            IOptions<AcsOptions> options,
            ILogger<AcsWhatsAppAdapter> logger)
        {
            _messagesClient = messagesClient;
            _logger = logger;

            var configured = options.Value.ChannelRegistrationId;
            if (!Guid.TryParse(configured, out _channelRegistrationId))
            {
                throw new InvalidOperationException(
                    "Acs:ChannelRegistrationId falta o no es un GUID valido. " +
                    "Se obtiene del canal de WhatsApp registrado en el recurso de ACS.");
            }

            // Mismo comportamiento que traia AdapterWithErrorHandler: que una excepcion del
            // bot no se pierda en silencio.
            OnTurnError = async (turnContext, exception) =>
            {
                _logger.LogError(exception, "[OnTurnError] error no manejado: {Message}", exception.Message);

                // Si lo que fallo fue el propio envio a ACS, avisarle al usuario implica
                // enviar por el mismo canal que acaba de fallar: no tiene sentido intentarlo.
                if (exception is RequestFailedException)
                {
                    return;
                }

                try
                {
                    await turnContext.SendActivityAsync(
                        "Hubo un problema procesando tu mensaje. Intentalo de nuevo en un momento.");
                }
                catch (Exception avisoFallido)
                {
                    _logger.LogError(avisoFallido,
                        "Tampoco se pudo avisarle al usuario del error: el canal de ACS no responde.");
                }
            };
        }

        //recibe el activity, el bot y lo prende
        public async Task ProcessActivityAsync(Activity activity, IBot bot, CancellationToken cancellationToken)
        {
            // No hay JWT de Azure Bot Service que validar: la confianza viene de la
            // suscripcion de Event Grid. Se arma el TurnContext a mano y se corre el pipeline.
            using (var turnContext = new TurnContext(this, activity))
            {
                await RunPipelineAsync(turnContext, bot.OnTurnAsync, cancellationToken);
            }
        }

        //el bot usa este metodo para mandar Activities devuelta al internet
        public override async Task<ResourceResponse[]> SendActivitiesAsync(
            ITurnContext turnContext, Activity[] activities, CancellationToken cancellationToken)
        {
            var responses = new List<ResourceResponse>(); // creo el ResourceResponce

            foreach (var activity in activities)
            {
                // WhatsApp no soporta indicador de "escribiendo" ni delays via ACS.
                if (activity.Type != ActivityTypes.Message)
                {
                    _logger.LogDebug("Activity de tipo {Type} ignorada: no aplica en WhatsApp.", activity.Type);
                    responses.Add(new ResourceResponse());
                    continue;
                }

                var destinatario = new[] { activity.Conversation.Id };

                try
                {
                    var attachment = activity.Attachments?.FirstOrDefault(a => !string.IsNullOrEmpty(a.ContentUrl));
                    var resultado = attachment != null
                        ? await SendAttachmentAsync(destinatario, attachment, activity.Text, cancellationToken)
                        : await SendTextAsync(destinatario, activity, cancellationToken);

                    responses.Add(new ResourceResponse(resultado));
                }
                catch (RequestFailedException ex)
                {
                    var causa = DiagnoseSendFailure(ex.Status);

                    _logger.LogError(ex,
                        "ACS rechazo el envio a {Destinatario} (status {Status}): {Message}. {Causa}",
                        activity.Conversation.Id, ex.Status, ex.Message, causa);
                    throw;
                }
            }

            return responses.ToArray();
        }

        //traduce el status HTTP que devolvio ACS a la causa probable, para que el log
        //diga que hacer en vez de solo que fallo
        private static string DiagnoseSendFailure(int status)
        {
            switch (status)
            {
                case 400:
                    return "Pedido invalido: numero mal formado, texto demasiado largo, " +
                           "o una URL de media que ACS no pudo descargar.";

                case 401:
                    return "No autenticado: revisa Acs:ConnectionString (vencida o mal copiada).";

                case 403:
                    return "Autenticado pero sin permiso sobre este canal de WhatsApp. " +
                           "Revisa el recurso de ACS y sus permisos.";

                case 404:
                    return "No existe el canal: revisa Acs:ChannelRegistrationId.";

                case 429:
                    return "Rate limit de ACS o de WhatsApp. Hay que reintentar con backoff.";

                // 470 es el codigo con el que ACS marca la ventana de 24 h cerrada.
                case 470:
                    return "Ventana de 24 h cerrada: el usuario no escribe hace mas de 24 h, " +
                           "asi que solo se acepta una plantilla preaprobada, no texto libre.";

                default:
                    if (status >= 500)
                    {
                        return "Error del lado de ACS. Suele ser transitorio: conviene reintentar.";
                    }

                    return "Causa no identificada: mirar el cuerpo de la respuesta de ACS.";
            }
        }

        private async Task<string> SendTextAsync(
            string[] destinatario, Activity activity, CancellationToken cancellationToken)
        {
            // SuggestedActions no tiene traduccion automatica a WhatsApp: los interactivos
            // nativos tienen limites duros (3 botones, o lista de 10) y hay que emitirlos
            // explicitamente. Por ahora se degradan a texto numerado.
            var texto = FlattenSuggestedActions(activity);

            var contenido = new TextNotificationContent(_channelRegistrationId, destinatario, texto);
            var respuesta = await _messagesClient.SendAsync(contenido, cancellationToken);
            return FirstMessageId(respuesta.Value);
        }

        private async Task<string> SendAttachmentAsync(
            string[] destinatario, Attachment attachment, string caption, CancellationToken cancellationToken)
        {
            var url = new Uri(attachment.ContentUrl);
            var tipo = attachment.ContentType ?? string.Empty;

            NotificationContent contenido;
            if (tipo.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                contenido = new ImageNotificationContent(_channelRegistrationId, destinatario, url) { Caption = caption };
            }
            else if (tipo.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            {
                contenido = new VideoNotificationContent(_channelRegistrationId, destinatario, url) { Caption = caption };
            }
            else if (tipo.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            {
                contenido = new AudioNotificationContent(_channelRegistrationId, destinatario, url);
            }
            else
            {
                contenido = new DocumentNotificationContent(_channelRegistrationId, destinatario, url)
                {
                    Caption = caption,
                    FileName = attachment.Name,
                };
            }

            var respuesta = await _messagesClient.SendAsync(contenido, cancellationToken);
            return FirstMessageId(respuesta.Value);
        }

        private static string FlattenSuggestedActions(Activity activity)
        {
            var acciones = activity.SuggestedActions?.Actions;
            if (acciones == null || acciones.Count == 0)
            {
                return activity.Text;
            }

            var opciones = acciones.Select((a, i) => $"{i + 1}. {a.Title}");
            return $"{activity.Text}\n\n{string.Join("\n", opciones)}";
        }

        private static string FirstMessageId(SendMessageResult resultado)
        {
            return resultado?.Receipts?.FirstOrDefault()?.MessageId ?? string.Empty;
        }



        //SDK te obliga a overridear estas funciones pero no sirven para la api de whats asi que no hacen nada >:(
        public override Task<ResourceResponse> UpdateActivityAsync(
            ITurnContext turnContext, Activity activity, CancellationToken cancellationToken)
            => throw new NotSupportedException("WhatsApp no permite editar mensajes ya enviados.");
        public override Task DeleteActivityAsync(
          ITurnContext turnContext, ConversationReference reference, CancellationToken cancellationToken)
          => throw new NotSupportedException("WhatsApp no permite borrar mensajes ya enviados.");

    }
}
