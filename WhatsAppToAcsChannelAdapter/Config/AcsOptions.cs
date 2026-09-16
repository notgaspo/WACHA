namespace WhatsAppToAcsChannelAdapter.Config
{
    /// <summary>
    /// Configuracion del recurso de Azure Communication Services.
    /// Se enlaza desde la seccion "Acs" de la configuracion.
    /// </summary>
    public class AcsOptions
    {
        public const string SectionName = "Acs";

        /// <summary>
        /// Connection string del recurso de ACS. Es un SECRETO: en desarrollo va por
        /// "dotnet user-secrets", en Azure por App Settings o Key Vault. Nunca en el repo.
        /// </summary>
        public string ConnectionString { get; set; }

        /// <summary>
        /// Id del canal de WhatsApp registrado en ACS (un GUID). Identifica el numero
        /// desde el cual se envian los mensajes.
        /// </summary>
        public string ChannelRegistrationId { get; set; }
    }
}
