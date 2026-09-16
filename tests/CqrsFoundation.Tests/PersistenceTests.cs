using CqrsFoundation.Application;
using CqrsFoundation.Auth;
using CqrsFoundation.Domain.Common;
using CqrsFoundation.Domain.Customers;
using CqrsFoundation.Domain.Tenants;
using CqrsFoundation.Infrastructure;
using Marten;
using Microsoft.AspNetCore.Identity;
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
        var actorId = Guid.NewGuid();
        var metadata = new CommandMetadata("integration-test", "integration-test-command");
        await SeedTenant(store, tenantA, actorId);

        await using (var session = store.LightweightSession(SystemTenancy.For(tenantA)))
        {
            AuditMetadata.Apply(session, actorId, metadata);
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
            Assert.AreEqual(metadata.CorrelationId, history[0].CorrelationId);
            Assert.AreEqual(metadata.CausationId, history[0].CausationId);
        }

        var historyItems = await CustomerQueries.History(
            tenantA,
            actorId,
            customerId,
            store,
            CancellationToken.None);
        Assert.HasCount(1, historyItems);
        Assert.AreEqual(metadata.CorrelationId, historyItems[0].CorrelationId);
        Assert.AreEqual(metadata.CausationId, historyItems[0].CausationId);

        await using (var query = store.QuerySession(SystemTenancy.For(tenantB)))
        {
            Assert.IsNull(await query.LoadAsync<CustomerView>(customerId));
            Assert.HasCount(0, await query.Events.FetchStreamAsync(customerId));
        }
    }

    [TestMethod]
    public async Task Unauthorized_member_is_rejected_before_target_user_existence_is_checked()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("TEST_POSTGRES is not configured.");
            return;
        }

        using var store = DocumentStore.For(options => Persistence.Configure(options, connectionString));
        var tenantId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        await SeedTenant(store, tenantId, ownerId, (memberId, TenantRoles.Member));

        try
        {
            await AddTenantMemberHandler.Handle(
                new AddTenantMember(tenantId, Guid.NewGuid(), TenantRoles.Member),
                memberId,
                store,
                new CommandMetadata("authorization-order-test", "authorization-order-test-command"),
                CancellationToken.None);
            Assert.Fail("Expected an authorization failure.");
        }
        catch (ForbiddenAccessException)
        {
            // The caller must be rejected before target-user existence is observable.
        }
    }

    [TestMethod]
    public async Task Direct_customer_write_handler_rejects_read_only_member()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("TEST_POSTGRES is not configured.");
            return;
        }

        using var store = DocumentStore.For(options => Persistence.Configure(options, connectionString));
        var tenantId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        await SeedTenant(store, tenantId, ownerId, (memberId, TenantRoles.Member));

        await Assert.ThrowsExactlyAsync<ForbiddenAccessException>(() =>
            CreateCustomerHandler.Handle(
                new CreateCustomer(tenantId, "Denied customer"),
                memberId,
                store,
                new CommandMetadata("direct-write", "direct-write-request"),
                CancellationToken.None));

        await using var query = store.QuerySession(SystemTenancy.For(tenantId));
        Assert.HasCount(0, await query.Query<CustomerView>().ToListAsync());
    }

    [TestMethod]
    public async Task Direct_customer_query_rejects_non_member()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("TEST_POSTGRES is not configured.");
            return;
        }

        using var store = DocumentStore.For(options => Persistence.Configure(options, connectionString));
        var tenantId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        await SeedTenant(store, tenantId, ownerId);

        await Assert.ThrowsExactlyAsync<ForbiddenAccessException>(() =>
            CustomerQueries.List(
                tenantId,
                Guid.NewGuid(),
                store,
                CancellationToken.None));
    }

    [TestMethod]
    public async Task Retried_create_tenant_returns_the_original_tenant_without_duplicate_events()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("TEST_POSTGRES is not configured.");
            return;
        }

        using var store = DocumentStore.For(options => Persistence.Configure(options, connectionString));
        var actorId = Guid.NewGuid();
        var key = $"tenant-{Guid.NewGuid():N}";

        var first = await CreateTenantHandler.Handle(
            new CreateTenant("Retry tenant"),
            actorId,
            store,
            new CommandMetadata("tenant-first", "tenant-first-request", key),
            CancellationToken.None);
        var retry = await CreateTenantHandler.Handle(
            new CreateTenant("Retry tenant"),
            actorId,
            store,
            new CommandMetadata("tenant-retry", "tenant-retry-request", key),
            CancellationToken.None);

        Assert.AreEqual(first, retry);
        await using var query = store.QuerySession(SystemTenancy.For(first));
        Assert.HasCount(1, await query.Events.FetchStreamAsync(first));
    }

    [TestMethod]
    public async Task Concurrent_create_customer_retries_converge_to_one_stream()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("TEST_POSTGRES is not configured.");
            return;
        }

        using var store = DocumentStore.For(options => Persistence.Configure(options, connectionString));
        var tenantId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var key = $"customer-{Guid.NewGuid():N}";
        var command = new CreateCustomer(tenantId, "Concurrent retry customer");
        await SeedTenant(store, tenantId, actorId);

        var first = CreateCustomerHandler.Handle(
            command,
            actorId,
            store,
            new CommandMetadata("customer-first", "customer-first-request", key),
            CancellationToken.None);
        var retry = CreateCustomerHandler.Handle(
            command,
            actorId,
            store,
            new CommandMetadata("customer-retry", "customer-retry-request", key),
            CancellationToken.None);
        var ids = await Task.WhenAll(first, retry);

        Assert.AreEqual(ids[0], ids[1]);
        await using var query = store.QuerySession(SystemTenancy.For(tenantId));
        Assert.HasCount(1, await query.Events.FetchStreamAsync(ids[0]));
    }

    [TestMethod]
    public async Task Reusing_an_idempotency_key_for_a_different_command_is_rejected()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("TEST_POSTGRES is not configured.");
            return;
        }

        using var store = DocumentStore.For(options => Persistence.Configure(options, connectionString));
        var tenantId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var key = $"reuse-{Guid.NewGuid():N}";
        var metadata = new CommandMetadata("reuse", "reuse-request", key);
        await SeedTenant(store, tenantId, actorId);

        var customerId = await CreateCustomerHandler.Handle(
            new CreateCustomer(tenantId, "Original"),
            actorId,
            store,
            metadata,
            CancellationToken.None);

        try
        {
            await CreateCustomerHandler.Handle(
                new CreateCustomer(tenantId, "Different"),
                actorId,
                store,
                metadata,
                CancellationToken.None);
            Assert.Fail("Expected the idempotency key reuse to be rejected.");
        }
        catch (BusinessRuleException exception)
        {
            StringAssert.Contains(exception.Message, "idempotency key");
        }

        await using var query = store.QuerySession(SystemTenancy.For(tenantId));
        Assert.HasCount(1, await query.Events.FetchStreamAsync(customerId));
    }

    [TestMethod]
    public async Task Idempotent_customer_retry_rechecks_current_permission()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("TEST_POSTGRES is not configured.");
            return;
        }

        using var store = DocumentStore.For(options => Persistence.Configure(options, connectionString));
        var tenantId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var key = $"permission-replay-{Guid.NewGuid():N}";
        await SeedTenant(store, tenantId, ownerId, (adminId, TenantRoles.Admin));

        var customerId = await CreateCustomerHandler.Handle(
            new CreateCustomer(tenantId, "Authorized once"),
            adminId,
            store,
            new CommandMetadata("permission-first", "permission-first-request", key),
            CancellationToken.None);

        await ChangeTenantMemberRoleHandler.Handle(
            new ChangeTenantMemberRole(tenantId, adminId, TenantRoles.Member),
            ownerId,
            store,
            new CommandMetadata("permission-revoke", "permission-revoke-request"),
            CancellationToken.None);

        await Assert.ThrowsExactlyAsync<ForbiddenAccessException>(() =>
            CreateCustomerHandler.Handle(
                new CreateCustomer(tenantId, "Authorized once"),
                adminId,
                store,
                new CommandMetadata("permission-retry", "permission-retry-request", key),
                CancellationToken.None));

        await using var query = store.QuerySession(SystemTenancy.For(tenantId));
        Assert.HasCount(1, await query.Events.FetchStreamAsync(customerId));
    }

    [TestMethod]
    public async Task Retried_registration_reuses_the_user_and_revalidates_the_password()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("TEST_POSTGRES is not configured.");
            return;
        }

        using var store = DocumentStore.For(options => Persistence.Configure(options, connectionString));
        var passwordHasher = new PasswordHasher<Credential>();
        var tokenService = new TokenService(
            new JwtOptions
            {
                Issuer = "cqrs-foundation-tests",
                Audience = "cqrs-foundation-tests",
                Key = "cqrs-foundation-tests-signing-key-32-bytes-minimum"
            },
            TimeProvider.System);
        var email = $"retry-{Guid.NewGuid():N}@example.test";
        var key = $"register-{Guid.NewGuid():N}";
        var password = "correct horse battery staple";

        var first = await RegisterUserHandler.Handle(
            new RegisterUser(email, password),
            store,
            passwordHasher,
            tokenService,
            new CommandMetadata("register-first", "register-first-request", key),
            CancellationToken.None);
        var retry = await RegisterUserHandler.Handle(
            new RegisterUser(email, password),
            store,
            passwordHasher,
            tokenService,
            new CommandMetadata("register-retry", "register-retry-request", key),
            CancellationToken.None);

        Assert.AreEqual(first.UserId, retry.UserId);
        Assert.AreEqual(first.Email, retry.Email);
        await using (var query = store.QuerySession(SystemTenancy.Id))
        {
            Assert.HasCount(1, await query.Events.FetchStreamAsync(first.UserId));
        }

        try
        {
            await RegisterUserHandler.Handle(
                new RegisterUser(email, "different valid password"),
                store,
                passwordHasher,
                tokenService,
                new CommandMetadata("register-different", "register-different-request", key),
                CancellationToken.None);
            Assert.Fail("Expected changed registration credentials to be rejected.");
        }
        catch (BusinessRuleException exception)
        {
            StringAssert.Contains(exception.Message, "registration credentials");
        }
    }

    private static async Task SeedTenant(
        IDocumentStore store,
        Guid tenantId,
        Guid ownerId,
        params (Guid UserId, string Role)[] members)
    {
        await using var session = store.LightweightSession(SystemTenancy.For(tenantId));
        AuditMetadata.Apply(
            session,
            ownerId,
            new CommandMetadata("tenant-seed", $"tenant-seed-{tenantId:N}"));
        var events = new List<object>
        {
            new TenantCreated(tenantId, "Authorization test", ownerId)
        };
        events.AddRange(members.Select(member =>
            (object)new TenantMemberAdded(member.UserId, TenantRoles.Normalize(member.Role))));
        session.Events.StartStream<TenantAggregate>(tenantId, events.ToArray());
        await session.SaveChangesAsync();
    }
}
