using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Charly.Bots
{
    // ActivityHandler enruta cada Activity entrante al metodo OnXxxAsync que corresponda
    // segun su Type. Solo sobreescribis los que te interesan.
    public class BotCharly : ActivityHandler
    {
        // Se dispara cuando alguien entra a la conversacion (Activity type = "conversationUpdate").
        // OJO: el bot tambien es un "miembro", por eso se filtra por Recipient.Id.
        protected override async Task OnMembersAddedAsync(
            IList<ChannelAccount> membersAdded,
            ITurnContext<IConversationUpdateActivity> turnContext,
            CancellationToken cancellationToken)
        {
            foreach (var member in membersAdded)
            {
                if (member.Id != turnContext.Activity.Recipient.Id)
                {
                    await turnContext.SendActivityAsync(
                        MessageFactory.Text("Hola! Soy Charly. Escribi 'ayuda' para ver que se hacer."),
                        cancellationToken);
                }
            }
        }

        // El metodo central: se dispara con cada mensaje de texto del usuario.
        protected override async Task OnMessageActivityAsync(
            ITurnContext<IMessageActivity> turnContext,
            CancellationToken cancellationToken)
        {
            // turnContext.Activity es el Activity entrante completo.
            var texto = turnContext.Activity.Text?.Trim() ?? string.Empty; //saco el texto del mensaje
            var usuario = turnContext.Activity.From.Name; //saco el nombre del usuario que me lo envia

            switch (texto.ToLowerInvariant())
            {
                case "ayuda":
                    // MessageFactory.Text -> el helper mas simple para responder texto.
                    await turnContext.SendActivityAsync(
                        MessageFactory.Text("Comandos: 'ayuda', 'hola', 'opciones', 'quien soy'"),
                        cancellationToken);
                    break;

                case "hola":
                    await turnContext.SendActivityAsync(
                        MessageFactory.Text($"Hola {usuario}!"), cancellationToken);
                    break;

                case "quien soy":
                    // Datos utiles del Activity para debuggear / personalizar.
                    await turnContext.SendActivityAsync(
                        MessageFactory.Text(
                            $"Usuario: {usuario} (id: {turnContext.Activity.From.Id})\n\n" +
                            $"Canal: {turnContext.Activity.ChannelId}\n\n" +
                            $"Conversacion: {turnContext.Activity.Conversation.Id}"),
                        cancellationToken);
                    break;

                case "opciones":
                    // SuggestedActions: botones rapidos que el canal renderiza y que,
                    // al tocarlos, vuelven como un mensaje de texto normal.
                    var conBotones = MessageFactory.Text("Elegi una opcion:");
                    conBotones.SuggestedActions = new SuggestedActions
                    {
                        Actions = new List<CardAction>
                        {
                            new CardAction { Title = "Saludar",   Type = ActionTypes.ImBack, Value = "hola" },
                            new CardAction { Title = "Quien soy", Type = ActionTypes.ImBack, Value = "quien soy" },
                        },
                    };
                    await turnContext.SendActivityAsync(conBotones, cancellationToken);
                    break;

                default:
                    // El clasico echo: confirma que el ciclo request/response funciona.
                    await turnContext.SendActivityAsync(
                        MessageFactory.Text($"Dijiste: \"{texto}\""), cancellationToken);
                    break;
            }
        }
    }
}
