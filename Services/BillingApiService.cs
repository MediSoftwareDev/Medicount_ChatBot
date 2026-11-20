using Medicount.Chatbot.Domain;
using System.Threading.Tasks;

namespace Medicount.Chatbot.Services;

// Verification is a server-side SQL operation. Calling this application's own
// public controller via localhost fails behind IIS, HTTPS bindings and proxies.
public sealed class BillingApiService(PatientVerificationRepository repository)
{
    public Task<Verified?> VerifyAsync(string name, string dob, string account) =>
        repository.VerifyPatientAsync(name, dob, account);
}