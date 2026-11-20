using Microsoft.EntityFrameworkCore;

namespace Medicount.Chatbot.Data
{
    public class ChatDbContext :DbContext
    {
        public ChatDbContext(DbContextOptions<ChatDbContext> options) : base(options)
        {
        }
    }
}
