namespace HackathonIde.Models
{
    public class CodeHistory
    {
        public int Id { get; set; }
        public int ProjectId { get; set; }
        public Project? Project { get; set; }
        public string CodeSnapshot { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string Author { get; set; } = string.Empty; // Кто внес изменения
    }
}
