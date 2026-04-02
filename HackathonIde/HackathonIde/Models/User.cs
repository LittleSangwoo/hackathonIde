namespace HackathonIde.Models
{
    public class User
    {
        public int Id { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty; // Для продакшена тут должен быть хэш, но для хакатона сойдет и так
    }
}
