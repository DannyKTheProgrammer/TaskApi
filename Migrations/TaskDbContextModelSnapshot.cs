using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

[DbContext(typeof(TaskDbContext))]
partial class TaskDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder.HasAnnotation("ProductVersion", "9.0.0");

        modelBuilder.Entity("TaskItem", b =>
        {
            b.Property<Guid>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("uuid");

            b.Property<DateTimeOffset?>("CompletedAt")
                .HasColumnType("timestamp with time zone");

            b.Property<DateTimeOffset>("CreatedAt")
                .HasColumnType("timestamp with time zone");

            b.Property<bool>("IsCompleted")
                .HasColumnType("boolean");

            b.Property<int>("Priority")
                .HasColumnType("integer");

            b.Property<string>("Title")
                .IsRequired()
                .HasMaxLength(200)
                .HasColumnType("character varying(200)");

            b.Property<uint>("Version")
                .IsConcurrencyToken()
                .ValueGeneratedOnAddOrUpdate()
                .HasColumnType("xid")
                .HasColumnName("xmin");

            b.HasKey("Id");

            b.ToTable("Tasks");
        });
#pragma warning restore 612, 618
    }
}
