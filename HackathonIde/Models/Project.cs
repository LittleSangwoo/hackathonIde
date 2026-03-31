namespace HackathonIde.Models
{
    public class Project
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string CurrentCode { get; set; } = string.Empty;

        // Навигационное свойство
        public List<CodeHistory> Histories { get; set; } = new();
    }
}
