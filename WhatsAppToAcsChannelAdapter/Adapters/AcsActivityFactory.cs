using Azure.Messaging.EventGrid.SystemEvents;
using Microsoft.Bot.Schema;
using System;
using System.Collections.Generic;

namespace WhatsAppToAcsChannelAdapter.Adapters
{
    public static class AcsActivityFactory
    {
        public const string ChannelId = "whatsapp"; //esto ya se setea globalmente para cualquier activity entrante


        //LA FUNCION QUE PRENDE LA FABRICA
        public static Activity CreateMessageActivity(AcsMessageReceivedEventData evento)
        {
            var identidad = ResolveIdentity(evento); //esto es para resolver el Form y el Conversation

            var activity = new Activity
            {
                Type = ActivityTypes.Message,
                Id = evento.MessageId,
                Timestamp = evento.ReceivedTimestamp,
                ChannelId = ChannelId,

                // No existe Connector API: las respuestas salen por el SDK de ACS, no por
                // un POST a ServiceUrl. Se deja vacio a proposito.
                ServiceUrl = string.Empty,

                From = new ChannelAccount(id: identidad),
                Recipient = new ChannelAccount(id: evento.To),

                Conversation = new ConversationAccount(isGroup: false, id: identidad),

                Text = ExtractText(evento), //aca necesitamos la funcion auxiliar porque vienen
                //un millon de cosas en formato texto aparte del texto en si mismo jeje.
                TextFormat = TextFormatTypes.Plain,

                // El evento completo queda disponible para el bot (media, reacciones, contexto
                // de respuesta) sin tener que ampliar el mapeo.
                ChannelData = evento,
            };

            var adjunto = CreateAttachment(evento); //para cuando viene un adjunto
            if (adjunto != null)
            {
                activity.Attachments = new List<Attachment> { adjunto };
            }

            return activity;
        }

        //Funciones auxiliares para crear activity
        public static string ResolveIdentity(AcsMessageReceivedEventData evento)
        {
            if (!string.IsNullOrWhiteSpace(evento.FromBsuid))
            {
                return evento.FromBsuid;
            }

            if (!string.IsNullOrWhiteSpace(evento.From))
            {
                return evento.From;
            }

            throw new InvalidOperationException(
                $"El evento {evento.MessageId} no trae ni From ni FromBsuid: no hay a quien responder.");
        }
        private static string ExtractText(AcsMessageReceivedEventData evento)
        {
            var interactivo = evento.InteractiveContent;
            if (interactivo != null)
            {
                if (!string.IsNullOrEmpty(interactivo.ButtonReply?.Title))
                {
                    return interactivo.ButtonReply.Title;
                }

                if (!string.IsNullOrEmpty(interactivo.ListReply?.Title))
                {
                    return interactivo.ListReply.Title;
                }
            }

            // Boton de plantilla (quick reply): el payload es el valor util.
            if (!string.IsNullOrEmpty(evento.Button?.Payload))
            {
                return evento.Button.Payload;
            }

            if (!string.IsNullOrEmpty(evento.Button?.Text))
            {
                return evento.Button.Text;
            }

            return evento.Content ?? string.Empty;
        }
        private static Attachment CreateAttachment(AcsMessageReceivedEventData evento)
        {
            var media = evento.MediaContent;
            if (media == null || string.IsNullOrEmpty(media.MediaId))
            {
                return null;
            }

            return new Attachment
            {
                ContentType = media.MimeType,
                Name = media.FileName,
                Content = media.MediaId,
            };
        }
    }
}
