using HackathonIde.Models;
using Microsoft.EntityFrameworkCore;

namespace HackathonIde.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<Project> Projects => Set<Project>();
        public DbSet<CodeHistory> CodeHistories => Set<CodeHistory>();
        public DbSet<User> Users => Set<User>();
    }
}
