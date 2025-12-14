using OAuthLogin.Services;
using OAuthLogin.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddOpenApi();

// Register Database Context
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("PostgresConnection")));

// Register Google Analytics Services
builder.Services.AddScoped<GoogleAnalyticsPermissionsService>();
builder.Services.AddScoped<GoogleAnalyticsReportService>();
builder.Services.AddScoped<GoogleSecretManagerService>();
builder.Services.AddScoped<TokenDatabaseService>();

// Register Kafka Services
builder.Services.AddSingleton<KafkaProducerService>();
builder.Services.AddSingleton<KafkaConsumerService>();
builder.Services.AddHttpClient("KafkaHttpSink");
builder.Services.AddHttpClient("KafkaJdbcSink");

// Register Worker Services
builder.Services.AddHostedService<AnalyticsWorkerService>();
builder.Services.AddHostedService<KafkaAnalyticsWorkerService>();
builder.Services.AddHostedService<KafkaHttpSinkWorkerService>();
builder.Services.AddHostedService<KafkaDataProcessorWorkerService>();
builder.Services.AddHostedService<KafkaJdbcSinkWorkerService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapControllers();


app.Run();

