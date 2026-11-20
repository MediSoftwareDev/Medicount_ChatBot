using System.ComponentModel.DataAnnotations;

namespace Medicount.Chatbot.Configuration
{
    public class InsuranceUpdateRequest
    {
        [Required]
        [MaxLength(150)]
        public string InsuranceCompany { get; set; } = "";

        [Required]
        [MaxLength(100)]
        public string MemberId { get; set; } = "";

        [MaxLength(100)]
        public string? GroupNumber { get; set; }

        // Optional for legacy API clients; reuse this GUID when retrying a request.
        [RegularExpression(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
        public string? RequestId { get; set; }
    }
}
