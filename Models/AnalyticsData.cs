using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OAuthLogin.Models
{
    [Table("raw_analytics_data")]
    public class RawAnalyticsData
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [MaxLength(255)]
        public string UserId { get; set; } = string.Empty;

        [Required]
        [MaxLength(255)]
        public string PropertyId { get; set; } = string.Empty;

        [Required]
        public DateTime Timestamp { get; set; }

        [Required]
        public string JsonData { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Required]
        [MaxLength(100)]
        public string Source { get; set; } = string.Empty; // Kafka topic source
    }

    [Table("normalized_analytics_data")]
    public class NormalizedAnalyticsData
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [MaxLength(255)]
        public string UserId { get; set; } = string.Empty;

        [Required]
        [MaxLength(255)]
        public string PropertyId { get; set; } = string.Empty;

        [Required]
        public DateTime EventDate { get; set; }

        [Required]
        [MaxLength(100)]
        public string Country { get; set; } = string.Empty;

        [Required]
        public int ActiveUsers { get; set; }

        [Required]
        public int Sessions { get; set; }

        [Required]
        public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;

        [Required]
        [MaxLength(100)]
        public string ProcessingSource { get; set; } = string.Empty; // Which processor created this record
    }

    [Table("analytics_processing_logs")]
    public class AnalyticsProcessingLog
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [MaxLength(255)]
        public string UserId { get; set; } = string.Empty;

        [Required]
        [MaxLength(255)]
        public string PropertyId { get; set; } = string.Empty;

        [Required]
        public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;

        [Required]
        public int RecordsProcessed { get; set; }

        [Required]
        public int RecordsNormalized { get; set; }

        [Required]
        public int RecordsStoredInFirebase { get; set; }

        [Required]
        public int RecordsStoredInPostgresql { get; set; }

        [Required]
        public int RecordsStoredInBigQuery { get; set; }

        [Required]
        [MaxLength(1000)]
        public string ErrorMessage { get; set; } = string.Empty;

        [Required]
        [MaxLength(100)]
        public string ProcessorType { get; set; } = string.Empty; // "Direct" or "JDBC"
    }

    // Firebase Firestore model
    public class FirestoreAnalyticsData
    {
        [Required]
        [MaxLength(255)]
        public string UserId { get; set; } = string.Empty;

        [Required]
        [MaxLength(255)]
        public string PropertyId { get; set; } = string.Empty;

        [Required]
        public DateTime EventDate { get; set; }

        [Required]
        [MaxLength(100)]
        public string Country { get; set; } = string.Empty;

        [Required]
        public int ActiveUsers { get; set; }

        [Required]
        public int Sessions { get; set; }

        [Required]
        public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;

        [Required]
        [MaxLength(100)]
        public string ProcessingSource { get; set; } = string.Empty;

        public string DocumentId { get; set; } = string.Empty; // Will be set by Firestore
    }

    // BigQuery model
    public class BigQueryAnalyticsData
    {
        [Required]
        [MaxLength(255)]
        public string UserId { get; set; } = string.Empty;

        [Required]
        [MaxLength(255)]
        public string PropertyId { get; set; } = string.Empty;

        [Required]
        public DateTime EventDate { get; set; }

        [Required]
        [MaxLength(100)]
        public string Country { get; set; } = string.Empty;

        [Required]
        public long ActiveUsers { get; set; }

        [Required]
        public long Sessions { get; set; }

        [Required]
        public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;

        [Required]
        [MaxLength(100)]
        public string ProcessingSource { get; set; } = string.Empty;
    }
}