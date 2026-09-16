using Azure.Communication.Messages;
using WhatsAppToAcsChannelAdapter.Adapters;
using WhatsAppToAcsChannelAdapter.Controllers;
using Charly.Bots;
using WhatsAppToAcsChannelAdapter.Config;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Bot.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System;

namespace Charly
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            // AddApplicationPart: sin esto ASP.NET no descubre el EventGridController,
            // porque vive en el assembly de la libreria y no en el de esta app.
            services.AddControllers()
                .AddApplicationPart(typeof(EventGridController).Assembly);

            services.Configure<AcsOptions>(Configuration.GetSection(AcsOptions.SectionName));

            // Cliente de ACS: es thread-safe y caro de crear, va como singleton.
            services.AddSingleton(sp =>
            {
                var options = sp.GetRequiredService<IOptions<AcsOptions>>().Value;

                if (string.IsNullOrWhiteSpace(options.ConnectionString))
                {
                    throw new InvalidOperationException(
                        "Falta Acs:ConnectionString. En desarrollo: " +
                        "dotnet user-secrets set \"Acs:ConnectionString\" \"<connection string>\". " +
                        "En Azure: App Settings o Key Vault.");
                }

                return new NotificationMessagesClient(options.ConnectionString);
            });

            // El adapter mantiene el cliente y el OnTurnError: singleton.
            services.AddSingleton<AcsWhatsAppAdapter>();

            // Transient: una instancia de bot por turn. Por eso el estado NO puede vivir
            // en campos de la clase.
            services.AddTransient<IBot, BotCharly>();
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseDefaultFiles()
                .UseStaticFiles()
                .UseRouting()
                .UseAuthorization()
                .UseEndpoints(endpoints =>
                {
                    endpoints.MapControllers();
                });
        }
    }
}
