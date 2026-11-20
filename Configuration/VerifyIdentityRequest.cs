using System.ComponentModel.DataAnnotations;

namespace Medicount.Chatbot.Configuration
{
    public class VerifyIdentityRequest
    {
        [Required(ErrorMessage = "Patient name is required.")]
        [MaxLength(150)]
        public string Name { get; set; } = string.Empty;

        [Required(ErrorMessage = "Date of birth is required.")]
        [MaxLength(10)]
        public string Dob { get; set; } = string.Empty;

        [Required(ErrorMessage = "Account number is required.")]
        [MaxLength(50)]
        public string Account { get; set; } = string.Empty;
    }
}
