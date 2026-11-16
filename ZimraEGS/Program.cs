using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Localization;
using System.Globalization;
using System.Text.Json.Serialization;
using ZimraEGS.ApiClient.Helpers;
using Microsoft.EntityFrameworkCore;
using ZimraEGS.Data;
using ZimraEGS.Helpers;
using ZimraEGS.Services;

namespace ZimraEGS
{
    public class Program
    {
        public static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            var builder = WebApplication.CreateBuilder(args);

            string logDirectory = Environment.GetEnvironmentVariable("HOME") != null
            ? Path.Combine(Environment.GetEnvironmentVariable("HOME"), "LogFiles", "MyApp") // Azure
            : Path.Combine(AppContext.BaseDirectory, "Logs"); // Local

            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }

            // Konfigurasi logging
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole();
            builder.Logging.AddProvider(new FileLoggerProvider(logDirectory, LogLevel.Information));


            builder.Services.Configure<JsonOptions>(options =>
                options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault | JsonIgnoreCondition.WhenWritingNull);

            builder.Services.AddCors(options =>
            {
                options.AddPolicy("AllowAllOrigins",
                    builder => builder.AllowAnyOrigin()
                                      .AllowAnyMethod()
                                      .AllowAnyHeader());
            });


            // Device keys, counters and receipts live in a database on this server.
            // "DataDirectory" must be a folder that survives restarts and is backed up.
            string dataDirectory = builder.Configuration["DataDirectory"] is { Length: > 0 } configured
                ? Path.GetFullPath(configured)
                : Environment.GetEnvironmentVariable("HOME") != null
                    ? Path.Combine(Environment.GetEnvironmentVariable("HOME")!, "ZimraEGS-data")
                    : Path.Combine(AppContext.BaseDirectory, "Data");
            Directory.CreateDirectory(dataDirectory);

            string connectionString = builder.Configuration.GetConnectionString("Fiscal") is { Length: > 0 } cs
                ? cs
                : $"Data Source={Path.Combine(dataDirectory, "zimraegs.db")}";

            builder.Services.AddDbContext<FiscalDbContext>(options => options.UseSqlite(connectionString));

            // Encrypts device private keys at rest. The key ring is stored next to the database:
            // back both up together, one is useless without the other.
            builder.Services.AddDataProtection()
                .SetApplicationName("ZimraEGS")
                .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));

            builder.Services.Configure<FdmsOptions>(builder.Configuration.GetSection("Fdms"));
            builder.Services.Configure<FiscalServiceOptions>(builder.Configuration.GetSection("Fiscal"));
            builder.Services.Configure<AutoCloseDayOptions>(builder.Configuration.GetSection("AutoCloseDay"));
            builder.Services.Configure<BillingOptions>(builder.Configuration.GetSection("Billing"));
            builder.Services.Configure<PrivacyOptions>(builder.Configuration.GetSection("Privacy"));
            bool httpsOnly = builder.Configuration.GetValue<bool>("Security:HttpsOnly");

            builder.Services.AddSingleton<IFdmsClientFactory, FdmsClientFactory>();
            builder.Services.AddSingleton<DeviceLocks>();
            builder.Services.AddSingleton<HsCodeCatalog>();
            builder.Services.AddScoped<DeviceStore>();
            builder.Services.AddScoped<BillingService>();
            builder.Services.AddScoped<PrivacyService>();
            builder.Services.AddSingleton(new LogDirectory(logDirectory));
            builder.Services.AddHostedService<PrivacyMaintenanceService>();
            builder.Services.AddScoped<FiscalService>();

            // Customer and admin sign-in for the account pages. The Manager panel does not use it: it works with
            // the device link stored in Manager, because cookies are not sent into a frame on another site.
            builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(o =>
                {
                    o.LoginPath = "/account/signin";
                    o.LogoutPath = "/account/signout";
                    o.AccessDeniedPath = "/account";
                    o.Cookie.Name = "zimraegs.account";
                    o.Cookie.HttpOnly = true;
                    o.Cookie.SameSite = SameSiteMode.Lax;
                    // With Security:HttpsOnly the sign-in cookie is never sent over plain http.
                    o.Cookie.SecurePolicy = httpsOnly ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
                    o.ExpireTimeSpan = TimeSpan.FromHours(12);
                    o.SlidingExpiration = true;
                });
            builder.Services.AddAuthorization(o => o.AddPolicy("Admin", p => p.RequireClaim("zimraegs:admin", "true")));
            builder.Services.AddHostedService<AutoCloseDayService>();

            // Add services to the container.
            builder.Services.AddControllersWithViews();

            var app = builder.Build();

            using (var scope = app.Services.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<FiscalDbContext>().Database.Migrate();
            }

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Home/Error");
            }

            if (httpsOnly)
            {
                app.UseHsts();
                app.UseHttpsRedirection();
            }

            // Basic protection for the pages that show customers' data. The extension pages are shown inside
            // Manager, in a frame, so only the account and admin pages refuse to be framed.
            app.Use(async (context, next) =>
            {
                var headers = context.Response.Headers;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                var path = context.Request.Path;
                if (path.StartsWithSegments("/account") || path.StartsWithSegments("/admin"))
                {
                    headers["X-Frame-Options"] = "DENY";
                    headers["Cache-Control"] = "no-store";
                }
                await next();
            });
            app.UseStaticFiles(new StaticFileOptions
            {
                // The panel's files must not be served stale from a browser cache after an update.
                OnPrepareResponse = ctx =>
                {
                    if (ctx.Context.Request.Path.StartsWithSegments("/extension"))
                    {
                        ctx.Context.Response.Headers.CacheControl = "no-cache";
                    }
                }
            });

            app.UseRouting();

            app.UseCors("AllowAllOrigins");

            var defaultCulture = new CultureInfo("en-US");

            var localizationOptions = new RequestLocalizationOptions
            {
                DefaultRequestCulture = new RequestCulture(defaultCulture),
                SupportedCultures = new List<CultureInfo> { defaultCulture },
                SupportedUICultures = new List<CultureInfo> { defaultCulture }
            };

            app.UseRequestLocalization(localizationOptions);


            app.UseAuthentication();
            app.UseAuthorization();

            app.MapControllerRoute(
                name: "default",
                pattern: "{controller=Home}/{action=Index}/{id?}");

            app.Run();
        }
    }
}
