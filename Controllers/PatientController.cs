using Medicount.Chatbot.Configuration;
using Medicount.Chatbot.Services;
using Medicount.Chatbot.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;

namespace Medicount.Chatbot.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PatientController : ControllerBase
    {
        private readonly PatientVerificationRepository _repository;
        private readonly AppDiagnostics? diagnostics;

        public PatientController(
            PatientVerificationRepository repository, AppDiagnostics? diagnostics = null)
        {
            _repository = repository;
            this.diagnostics = diagnostics;
        }

        [HttpPost("VerifyPatient")]
        public async Task<IActionResult> VerifyPatient([FromBody] VerifyIdentityRequest request)
        {
            // Failed or unavailable verification must never retain an earlier identity.
            HttpContext.Session.Remove("PatientVerified");
            HttpContext.Session.Remove("VerifiedClientId");
            HttpContext.Session.Remove("VerifiedAccountKey");
            var attempts = HttpContext.Session.GetInt32("VerificationAttempts") ?? 0;
            if (attempts >= 3)
            {
                diagnostics?.Write("patient.verification", "locked_out", statusCode: 429);
                return StatusCode(429, new { ok = false, message = "Verification attempts exceeded. Please start a new session or contact patient support." });
            }
            var patient = await _repository.VerifyPatientAsync(request.Name, request.Dob, request.Account, HttpContext.RequestAborted);
            HttpContext.Session.SetInt32("VerificationAttempts", attempts + 1);

            if (patient == null)
                return Ok(new { ok = false });

            HttpContext.Session.SetString("PatientVerified", "true");
            HttpContext.Session.SetString("VerifiedClientId", patient.ClientId.ToString()!);
            HttpContext.Session.SetString("VerifiedAccountKey", patient.AccountKey.ToString()!);

            return Ok(new
            {
                ok = true,
                clientId = patient.ClientId,
                accountKey = patient.AccountKey,
                placed = patient.Placed,
                nextForm = "insurance_update",

                message = "Patient verified successfully. Please enter your insurance information."
            });
        }
    
        [HttpPost("UpdateInsurance")]
        public async Task<IActionResult> UpdateInsurance(    [FromBody] InsuranceUpdateRequest request)
        {
            // 1. Check whether the patient was verified.
            var verified = HttpContext.Session.GetString("PatientVerified");

            if (verified != "true")
            {
                diagnostics?.Write("patient.insurance", "verification_required", statusCode: 401);
                return Unauthorized(new
                {
                    ok = false,
                    message = "Please verify patient information first."
                });
            }

            // 2. Get verified patient context from session.
            var clientId = HttpContext.Session.GetString("VerifiedClientId");

            var accountKey = HttpContext.Session.GetString("VerifiedAccountKey");

            if (string.IsNullOrWhiteSpace(clientId) ||
                string.IsNullOrWhiteSpace(accountKey))
            {
                diagnostics?.Write("patient.insurance", "session_expired", statusCode: 401);
                return Unauthorized(new
                {
                    ok = false,
                    message = "Patient session expired. Please verify again."
                });
            }

            // 3. Check input validation.
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            // 4. Save request in database.
            // Implement this method in your repository.
            await _repository.SaveInsuranceUpdateRequestAsync(
                clientId,
                accountKey,
                request.InsuranceCompany.Trim(),
                request.MemberId.Trim(),
                request.GroupNumber?.Trim(),
                HttpContext.RequestAborted,
                request.RequestId
            );

            // 5. Return successful response.
            return Ok(new
            {
                ok = true,
                completed = true,
                message = "Your insurance update request has been received successfully."
            });
        }
    }
}
