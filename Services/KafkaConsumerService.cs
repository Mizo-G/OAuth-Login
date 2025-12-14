using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using OAuthLogin.Models;

namespace OAuthLogin.Services
{
    /// <summary>
    /// Service for consuming messages from Kafka topics using Confluent.Kafka client.
    /// Provides methods to consume batches of messages with proper error handling.
    /// Deserializes JSON messages to strongly-typed analytics objects.
    /// </summary>
    public class KafkaConsumerService : IDisposable
    {
        private readonly IConsumer<Ignore, string> _consumer;
        private readonly ILogger<KafkaConsumerService> _logger;
        private readonly string _topic;

        public KafkaConsumerService(IConfiguration configuration, ILogger<KafkaConsumerService> logger)
        {
            _logger = logger;
            
            var bootstrapServers = configuration["Kafka:BootstrapServers"] ?? "localhost:9092";
            _topic = configuration["Kafka:Topic"] ?? "analytics-reports";

            var consumerConfig = new ConsumerConfig
            {
                BootstrapServers = bootstrapServers,
                GroupId = "analytics-processor-group",
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false, // We'll commit manually
                EnableAutoOffsetStore = false
            };

            _consumer = new ConsumerBuilder<Ignore, string>(consumerConfig).Build();
            _logger.LogInformation("Kafka consumer initialized with bootstrap servers: {BootstrapServers}, topic: {Topic}", bootstrapServers, _topic);
        }

        public async Task<List<AnalyticsReportKafkaMessage>> ConsumeMessagesAsync(CancellationToken cancellationToken, int maxMessages = 100, TimeSpan? timeout = null)
        {
            var messages = new List<AnalyticsReportKafkaMessage>();
            
            try
            {
                _consumer.Subscribe(_topic);
                _logger.LogInformation("Subscribed to Kafka topic: {Topic}", _topic);

                var consumeTimeout = timeout ?? TimeSpan.FromSeconds(30);
                var consumedCount = 0;

                while (consumedCount < maxMessages && !cancellationToken.IsCancellationRequested)
                {
                    var consumeResult = _consumer.Consume(consumeTimeout);
                    
                    if (consumeResult.IsPartitionEOF)
                    {
                        _logger.LogInformation("Reached end of partition");
                        break;
                    }

                    if (consumeResult.Error != null)
                    {
                        _logger.LogError("Consumer error: {Error}", consumeResult.Error);
                        break;
                    }

                    if (consumeResult.Message != null)
                    {
                        try
                        {
                            var message = JsonSerializer.Deserialize<AnalyticsReportKafkaMessage>(consumeResult.Message.Value);
                            if (message != null)
                            {
                                messages.Add(message);
                                consumedCount++;
                                
                                _logger.LogDebug("Consumed message for user: {UserId}, rows: {RowCount}", 
                                    message.UserId, message.ReportData?.Count ?? 0);
                            }
                        }
                        catch (JsonException ex)
                        {
                            _logger.LogError(ex, "Error deserializing Kafka message: {Message}", consumeResult.Message.Value);
                        }
                    }

                    // Manually commit the offset
                    _consumer.Commit();
                }

                _logger.LogInformation("Consumed {Count} messages from Kafka topic: {Topic}", messages.Count, _topic);
                return messages;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error consuming messages from Kafka");
                throw;
            }
            finally
            {
                _consumer.Close();
            }
        }

        public void Dispose()
        {
            _consumer?.Dispose();
        }
    }
}