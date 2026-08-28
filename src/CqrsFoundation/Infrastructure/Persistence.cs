using CqrsFoundation.Auth;
using CqrsFoundation.Domain.Customers;
using CqrsFoundation.Domain.Tenants;
using CqrsFoundation.Domain.Users;
using Marten;
using Marten.Events;
using Marten.Events.Projections;

namespace CqrsFoundation.Infrastructure;

public static class SystemTenancy
{
    public const string Id = "system";

    public static string For(Guid tenantId) => tenantId.ToString("D");
}

public static class Persistence
{
    public static void Configure(StoreOptions options, string connectionString)
    {
        options.Connection(connectionString);
        options.DatabaseSchemaName = "cqrs_foundation";

        options.Events.TenancyStyle = TenancyStyle.Conjoined;
        options.Events.MetadataConfig.HeadersEnabled = true;
        options.Events.MetadataConfig.CausationIdEnabled = true;
        options.Events.MetadataConfig.CorrelationIdEnabled = true;
        options.Events.MetadataConfig.UserNameEnabled = true;

        options.Policies.AllDocumentsAreMultiTenanted();
        options.Schema.For<Credential>().SingleTenanted();
        options.Schema.For<Credential>().UniqueIndex(x => x.Email);

        options.Projections.Snapshot<UserAggregate>(SnapshotLifecycle.Inline);
        options.Projections.Snapshot<UserProfile>(SnapshotLifecycle.Inline);
        options.Projections.Snapshot<TenantAggregate>(SnapshotLifecycle.Inline);
        options.Projections.Snapshot<TenantView>(SnapshotLifecycle.Inline);
        options.Projections.Snapshot<CustomerAggregate>(SnapshotLifecycle.Inline);
        options.Projections.Snapshot<CustomerView>(SnapshotLifecycle.Inline);
    }
}

public static class AuditMetadata
{
    public static void Apply(IDocumentSession session, Guid actorId, string correlationId)
    {
        session.LastModifiedBy = actorId.ToString();
        session.CorrelationId = correlationId;
        session.SetHeader("actor_id", actorId.ToString());
    }
}
