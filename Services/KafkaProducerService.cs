using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace OAuthLogin.Services
{
    /// <summary>
    /// Service for producing messages to Kafka topics using Confluent.Kafka client.
    /// Provides methods to send structured analytics data to Kafka with proper headers.
    /// Configured for reliability with idempotence and retry mechanisms.
    /// </summary>
    public class KafkaProducerService : IDisposable
    {
        private readonly IProducer<Null, string> _producer;
        private readonly string _topic;
        private readonly ILogger<KafkaProducerService> _logger;

        public KafkaProducerService(IConfiguration configuration, ILogger<KafkaProducerService> logger)
        {
            _logger = logger;
            
            var bootstrapServers = configuration["Kafka:BootstrapServers"] ?? "localhost:9092";
            _topic = configuration["Kafka:Topic"] ?? "analytics-reports";

            var producerConfig = new ProducerConfig
            {
                BootstrapServers = bootstrapServers,
                ClientId = "analytics-producer",
                Acks = Acks.All,
                EnableIdempotence = true,
                RetryBackoffMs = 100,
                RetryBackoffMaxMs = 1000,
                MessageSendMaxRetries = 3
            };

            _producer = new ProducerBuilder<Null, string>(producerConfig).Build();
            _logger.LogInformation("Kafka producer initialized with bootstrap servers: {BootstrapServers}, topic: {Topic}", bootstrapServers, _topic);
        }

        public async Task ProduceReportAsync<T>(T reportData, string key = null)
        {
            try
            {
                var message = new Message<Null, string>
                {
                    Value = JsonSerializer.Serialize(reportData, new JsonSerializerOptions { WriteIndented = true }),
                    Headers = new Headers
                    {
                        { "content-type", System.Text.Encoding.UTF8.GetBytes("application/json") },
                        { "timestamp", System.Text.Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")) }
                    }
                };

                if (!string.IsNullOrEmpty(key))
                {
                    message.Key = key;
                }

                var deliveryResult = await _producer.ProduceAsync(_topic, message);
                
                _logger.LogInformation("Message produced to topic: {Topic}, partition: {Partition}, offset: {Offset}", 
                    deliveryResult.Topic, deliveryResult.Partition, deliveryResult.Offset);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error producing message to Kafka topic: {Topic}", _topic);
                throw;
            }
        }

        public void Dispose()
        {
            _producer?.Dispose();
        }
    }
}