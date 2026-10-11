using Microsoft.EntityFrameworkCore;

namespace TemporalGraph.Data;

/// <summary>One book's SQLite database. Use <see cref="BookDatabase"/> to create or open one.</summary>
public class BookDbContext(DbContextOptions<BookDbContext> options) : DbContext(options)
{
    public DbSet<Node> Nodes => Set<Node>();
    public DbSet<NodeLabel> NodeLabels => Set<NodeLabel>();
    public DbSet<Edge> Edges => Set<Edge>();
    public DbSet<Chapter> Chapters => Set<Chapter>();
    public DbSet<Position> Positions => Set<Position>();
    public DbSet<EventOrder> EventOrder => Set<EventOrder>();
    public DbSet<Character> Characters => Set<Character>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<TimelineRow> Timeline => Set<TimelineRow>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Node>(e =>
        {
            e.ToTable("nodes");
            e.Property(n => n.Id).HasColumnName("id");
            e.Property(n => n.Properties).HasColumnName("properties").HasDefaultValueSql("'{}'");
            e.Property(n => n.EvidenceStart).HasColumnName("evidence_start");
            e.Property(n => n.EvidenceEnd).HasColumnName("evidence_end");
            e.Property(n => n.RevealedPos).HasColumnName("revealed_pos");
            e.Property(n => n.RetractedPos).HasColumnName("retracted_pos");
            e.Property(n => n.StoryFromEdtf).HasColumnName("story_from_edtf");
            e.Property(n => n.StoryUntilEdtf).HasColumnName("story_until_edtf");
            e.Property(n => n.StoryFromLo).HasColumnName("story_from_lo");
            e.Property(n => n.StoryFromHi).HasColumnName("story_from_hi");
            e.Property(n => n.StoryUntilLo).HasColumnName("story_until_lo");
            e.Property(n => n.StoryUntilHi).HasColumnName("story_until_hi");
            e.Property(n => n.FabulaSeq).HasColumnName("fabula_seq");
            e.Property(n => n.LayoutX).HasColumnName("layout_x");
            e.Property(n => n.LayoutY).HasColumnName("layout_y");
            e.Property(n => n.LayoutZ).HasColumnName("layout_z");
            e.Property(n => n.Layout2X).HasColumnName("layout2_x");
            e.Property(n => n.Layout2Y).HasColumnName("layout2_y");
        });

        model.Entity<NodeLabel>(e =>
        {
            e.ToTable("node_labels");
            e.HasKey(l => new { l.NodeId, l.Label });
            e.Property(l => l.NodeId).HasColumnName("node_id");
            e.Property(l => l.Label).HasColumnName("label");
            e.HasOne(l => l.Node).WithMany(n => n.Labels).HasForeignKey(l => l.NodeId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(l => new { l.Label, l.NodeId }).HasDatabaseName("ix_node_labels_label_node_id");
        });

        // Edge indexes are GraphQLite's: (source_id, type), (target_id, type), (type).
        model.Entity<Edge>(e =>
        {
            e.ToTable("edges");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.SourceId).HasColumnName("source_id");
            e.Property(x => x.TargetId).HasColumnName("target_id");
            e.Property(x => x.Type).HasColumnName("type");
            e.Property(x => x.Properties).HasColumnName("properties").HasDefaultValueSql("'{}'");
            e.Property(x => x.RevealedPos).HasColumnName("revealed_pos");
            e.Property(x => x.RetractedPos).HasColumnName("retracted_pos");
            e.Property(x => x.EvidenceStart).HasColumnName("evidence_start");
            e.Property(x => x.EvidenceEnd).HasColumnName("evidence_end");
            e.Property(x => x.StoryFromEdtf).HasColumnName("story_from_edtf");
            e.Property(x => x.StoryUntilEdtf).HasColumnName("story_until_edtf");
            e.Property(x => x.StoryFromLo).HasColumnName("story_from_lo");
            e.Property(x => x.StoryFromHi).HasColumnName("story_from_hi");
            e.Property(x => x.StoryUntilLo).HasColumnName("story_until_lo");
            e.Property(x => x.StoryUntilHi).HasColumnName("story_until_hi");
            e.Property(x => x.FabulaSeq).HasColumnName("fabula_seq");
            // Cascade, as in GraphQLite's edges table.
            e.HasOne(x => x.Source).WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Target).WithMany().HasForeignKey(x => x.TargetId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.SourceId, x.Type }).HasDatabaseName("ix_edges_source_id_type");
            e.HasIndex(x => new { x.TargetId, x.Type }).HasDatabaseName("ix_edges_target_id_type");
            e.HasIndex(x => x.Type).HasDatabaseName("ix_edges_type");
        });

        model.Entity<Chapter>(e =>
        {
            e.ToTable("chapters");
            e.HasKey(c => c.Number);
            e.Property(c => c.Number).HasColumnName("number").ValueGeneratedNever();
            e.Property(c => c.Title).HasColumnName("title");
            e.Property(c => c.CharStart).HasColumnName("char_start");
            e.Property(c => c.CharEnd).HasColumnName("char_end");
            e.Property(c => c.Narrator).HasColumnName("narrator");
        });

        model.Entity<TimelineRow>(e =>
        {
            e.ToTable("timeline");
            e.Property(t => t.Id).HasColumnName("id");
            e.Property(t => t.Kind).HasColumnName("kind");
            e.Property(t => t.RefId).HasColumnName("ref_id");
            e.Property(t => t.Label).HasColumnName("label");
            e.Property(t => t.RevealedPos).HasColumnName("revealed_pos");
            e.Property(t => t.RevealedPosEnd).HasColumnName("revealed_pos_end");
            e.Property(t => t.StoryFromEdtf).HasColumnName("story_from_edtf");
            e.Property(t => t.StoryUntilEdtf).HasColumnName("story_until_edtf");
            e.Property(t => t.StoryFromLo).HasColumnName("story_from_lo");
            e.Property(t => t.StoryFromHi).HasColumnName("story_from_hi");
            e.Property(t => t.StoryUntilLo).HasColumnName("story_until_lo");
            e.Property(t => t.StoryUntilHi).HasColumnName("story_until_hi");
            e.Property(t => t.FabulaSeq).HasColumnName("fabula_seq");
            e.Property(t => t.Note).HasColumnName("note");
            e.HasIndex(t => t.RevealedPos).HasDatabaseName("ix_timeline_revealed_pos");
        });

        model.Entity<Position>(e =>
        {
            e.ToTable("positions");
            e.Property(p => p.Id).HasColumnName("id");
            e.Property(p => p.Chapter).HasColumnName("chapter");
            e.Property(p => p.Paragraph).HasColumnName("paragraph");
            e.Property(p => p.CharStart).HasColumnName("char_start");
            e.Property(p => p.CharEnd).HasColumnName("char_end");
            e.Property(p => p.SceneId).HasColumnName("scene_id");
            e.HasIndex(p => p.CharStart).HasDatabaseName("ix_positions_char_start");
        });

        // relation is also checked against TemporalRelations.All by the database.
        var relations = string.Join(", ", TemporalRelations.All.Select(r => "'" + r + "'"));
        model.Entity<EventOrder>(e =>
        {
            e.ToTable("event_order", t => t.HasCheckConstraint("ck_event_order_relation", "relation IN (" + relations + ")"));
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.AEdgeId).HasColumnName("a_edge_id");
            e.Property(x => x.BEdgeId).HasColumnName("b_edge_id");
            e.Property(x => x.Relation).HasColumnName("relation");
            e.HasOne(x => x.AEdge).WithMany().HasForeignKey(x => x.AEdgeId);
            e.HasOne(x => x.BEdge).WithMany().HasForeignKey(x => x.BEdgeId);
        });

        model.Entity<Character>(e =>
        {
            e.ToTable("characters");
            e.Property(c => c.Id).HasColumnName("id");
            e.Property(c => c.NodeId).HasColumnName("node_id");
            e.Property(c => c.Name).HasColumnName("name");
            e.Property(c => c.ShortName).HasColumnName("short_name");
            e.Property(c => c.Initials).HasColumnName("initials");
            e.HasOne(c => c.Node).WithMany().HasForeignKey(c => c.NodeId);
        });

        model.Entity<Location>(e =>
        {
            e.ToTable("locations");
            e.Property(l => l.Id).HasColumnName("id");
            e.Property(l => l.NodeId).HasColumnName("node_id");
            e.Property(l => l.Name).HasColumnName("name");
            e.HasOne(l => l.Node).WithMany().HasForeignKey(l => l.NodeId);
        });
    }
}
