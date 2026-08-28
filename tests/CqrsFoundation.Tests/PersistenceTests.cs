using CqrsFoundation.Domain.Customers;
using CqrsFoundation.Infrastructure;
using Marten;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CqrsFoundation.Tests;

[TestClass]
public sealed class PersistenceTests
{
    [TestMethod]
    public async Task Events_project_inline_and_stay_tenant_isolated()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("TEST_POSTGRES is not configured.");
            return;
        }

        using var store = DocumentStore.For(options => Persistence.Configure(options, connectionString));
        var customerId = Guid.NewGuid();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await using (var session = store.LightweightSession(SystemTenancy.For(tenantA)))
        {
            AuditMetadata.Apply(session, Guid.NewGuid(), "integration-test");
            session.Events.StartStream<CustomerAggregate>(
                customerId,
                new CustomerCreated(customerId, "Projected customer"));
            await session.SaveChangesAsync();
        }

        await using (var query = store.QuerySession(SystemTenancy.For(tenantA)))
        {
            var customer = await query.LoadAsync<CustomerView>(customerId);
            Assert.IsNotNull(customer);
            Assert.AreEqual("Projected customer", customer.Name);

            var history = await query.Events.FetchStreamAsync(customerId);
            Assert.HasCount(1, history);
            Assert.AreEqual("integration-test", history[0].CorrelationId);
        }

        await using (var query = store.QuerySession(SystemTenancy.For(tenantB)))
        {
            Assert.IsNull(await query.LoadAsync<CustomerView>(customerId));
            Assert.HasCount(0, await query.Events.FetchStreamAsync(customerId));
        }
    }
}
