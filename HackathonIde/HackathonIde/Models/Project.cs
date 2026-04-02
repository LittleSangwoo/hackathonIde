namespace HackathonIde.Models
{
    public class Project
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string CurrentCode { get; set; } = string.Empty;
        public string Password { get; set; } // Пароль для входа
        public string OwnerId { get; set; } // Кто создал

        // Навигационное свойство
        public List<CodeHistory> Histories { get; set; } = new();
    }
}
