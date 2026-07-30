using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;
using Serilog.Extensions.Logging;
using System.Text;
using velios.Api.Data;
using velios.Api.Models.Common;
using velios.Api.Services;
using velios.Api.Services.CodigosPostales;
using velios.Api.Services.Email;
using velios.Api.Services.Encuestas;
using velios.Api.Services.PresupuestoGuardado;
using velios.Api.Services.ProveedoresDocs;
using velios.Api.Services.Security;
using velios.Api.Services.ServiciosCategoria;
using velios.Api.Services.ServiciosProveedor;
using Serilog;
using Serilog.Extensions.Logging;


/// <summary>
/// Punto de entrada de Velios API (Minimal Hosting .NET 6+).
/// 
/// REGLA CLAVE:
/// - Todo builder.Services.* debe ir ANTES de builder.Build()
/// - Todo app.Use* y app.Map* debe ir DESPUÉS de builder.Build()
/// </summary>
var builder = WebApplication.CreateBuilder(args);

var logsPath = Path.Combine(builder.Environment.ContentRootPath, "logs", "app-.txt");

var serilogLogger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.File(
        path: logsPath,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}{NewLine}      {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

Console.WriteLine($"[Serilog] Logs escribiéndose en: {Path.GetDirectoryName(logsPath)}");
builder.Logging.AddProvider(new SerilogLoggerProvider(serilogLogger, dispose: true));

#region ============================= CONFIGURACIÓN DE SERVICIOS =============================


// ------------------------------------------------------------
// 1) Configuración de opciones (Settings)
// ------------------------------------------------------------

// SMTP settings (appsettings.json -> "Smtp")
builder.Services.Configure<SmtpSettings>(builder.Configuration.GetSection("Smtp"));

builder.Services.AddScoped<IServicioProveedorService, ServicioProveedorService>();

// ------------------------------------------------------------
// 2) Registro de servicios (DI - Dependency Injection)
// ------------------------------------------------------------

// Email sender
builder.Services.AddScoped<IEmailSender, BrevoSmtpEmailSender>();

// Códigos postales
builder.Services.AddScoped<ICodigoPostalService, CodigoPostalService>();

// Password hasher (legacy)
builder.Services.AddSingleton<IPasswordHasher, LegacyPasswordHasher>();

// Proveedor documentos
builder.Services.AddScoped<IProveedorDocumentService, ProveedorDocumentService>();

// Registro del módulo de reporte de materialidad
builder.Services.AddScoped<IReporteMaterialidadPreeliminarService, ReporteMaterialidadPreeliminarService>();
builder.Services.AddScoped<IReporteMaterialidadRepository, ReporteMaterialidadRepository>();
builder.Services.AddScoped<IReporteMaterialidadService, ReporteMaterialidadService>();
builder.Services.AddMemoryCache();

builder.Services.AddHttpClient();

// Servicios - Categorías
builder.Services.AddScoped<ICategoriaServicioService, CategoriaServicioService>();

// presupuestos guardados
builder.Services.AddScoped<IPresupuestoGuardadoService, PresupuestoGuardadoService>();

// servicios encuestas

builder.Services.AddScoped<IEncuestaService, EncuestaService>();
// ------------------------------------------------------------
// 3) MVC / Controllers
// ------------------------------------------------------------
builder.Services.AddControllers();


// ------------------------------------------------------------
// 4) Swagger/OpenAPI
// ------------------------------------------------------------
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Velios API", Version = "v1" });

    c.MapType<IFormFile>(() => new OpenApiSchema
    {
        Type = "string",
        Format = "binary"
    });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Ingresa el token JWT. Ejemplo: Bearer eyJhbGciOi..."
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// ------------------------------------------------------------
// 5) Base de datos (DbContext)
// ------------------------------------------------------------
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("VeliosConnection")));

builder.Services.AddDbContext<NomclickDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("nomclickConnection")));


// ------------------------------------------------------------
// 6) CORS (IMPORTANTE para llamadas desde Browser local)
// ------------------------------------------------------------
// Nota: Pon el nombre de la policy y úsala igual en app.UseCors("...")
// Si luego quieres restringir, cambia AllowAnyOrigin por WithOrigins("http://localhost:xxxx")
builder.Services.AddCors(opt =>
{
    opt.AddPolicy("LocalDevCors", p =>
        p.AllowAnyHeader()
         .AllowAnyMethod()
         .AllowAnyOrigin()
         // 💡 PERMITE QUE JAVASCRIPT LEA EL TAMAÑO DEL ARCHIVO Y LAS CABECERAS PERSONALIZADAS
         .WithExposedHeaders("Content-Length", "Content-Disposition", "X-Tiempo-Generacion"));
});

#endregion

#region ============================= CONFIGURACIÓN JWT =============================

// JWT Key
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new Exception("Jwt:Key missing (configura Jwt:Key en appsettings o variables de entorno)");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = false;
        options.SaveToken = true;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,

            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.FromMinutes(1)
        };

        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = context =>
            {
                Console.WriteLine("JWT Authentication failed: " + context.Exception.Message);
                return Task.CompletedTask;
            },
            OnChallenge = context =>
            {
                Console.WriteLine("JWT Challenge error: " + context.Error);
                Console.WriteLine("JWT Challenge description: " + context.ErrorDescription);
                return Task.CompletedTask;
            }
        };
    });
// Autorización (atributos [Authorize])
builder.Services.AddAuthorization();

#endregion

// ✅ A partir de aquí, ya NO se pueden modificar builder.Services
var app = builder.Build();

app.UseStaticFiles();

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(
        Path.Combine(builder.Environment.ContentRootPath, "Resources")),
    RequestPath = "/Resources"
});
#region ============================= PIPELINE HTTP =============================

// ------------------------------------------------------------
// Diagnóstico: confirma si la request llega al pipeline (antes de MVC)
// (No leer Body aquí, especialmente multipart)
// ------------------------------------------------------------
app.Use(async (ctx, next) =>
{
    app.Logger.LogInformation("Incoming: {Method} {Path}{Query} CT={CT} Len={Len}",
        ctx.Request.Method,
        ctx.Request.Path,
        ctx.Request.QueryString,
        ctx.Request.ContentType,
        ctx.Request.ContentLength);

    await next();
});


// ------------------------------------------------------------
// Swagger UI
// (si quieres solo en Development, envuélvelo en if(app.Environment.IsDevelopment()))
// ------------------------------------------------------------
app.UseSwagger();
app.UseSwaggerUI();


// ------------------------------------------------------------
// HTTPS Redirection
// ------------------------------------------------------------
app.UseHttpsRedirection();


// ------------------------------------------------------------
// CORS (DEBE IR ANTES de Authentication/Authorization)
// ------------------------------------------------------------
app.UseCors("LocalDevCors");


// ------------------------------------------------------------
// Seguridad: AuthN / AuthZ
// ------------------------------------------------------------
app.UseAuthentication();
app.UseAuthorization();


// ------------------------------------------------------------
// Endpoints Controllers
// ------------------------------------------------------------
app.MapControllers();

#endregion

Console.WriteLine($"API started at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

app.Run();