using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OAuthLogin.Models
{
    [Table("user_tokens")]
    public class TokenRecord
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [MaxLength(255)]
        public string UserId { get; set; } = string.Empty;

        [Required]
        [MaxLength(1000)]
        public string AccessToken { get; set; } = string.Empty;

        [Required]
        [MaxLength(1000)]
        public string RefreshToken { get; set; } = string.Empty;

        [Required]
        [MaxLength(50)]
        public string TokenType { get; set; } = string.Empty;

        public int? ExpiresIn { get; set; }

        [Required]
        [MaxLength(500)]
        public string Scope { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? ExpiresAt { get; set; }

        [Required]
        public bool IsActive { get; set; } = true;
    }
}