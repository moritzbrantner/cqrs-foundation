using CqrsFoundation.Domain.Common;
using CqrsFoundation.Domain.Customers;
using CqrsFoundation.Domain.Tenants;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CqrsFoundation.Tests;

[TestClass]
public sealed class DomainTests
{
    [TestMethod]
    public void Customer_decider_returns_events_without_mutating_state()
    {
        var id = Guid.NewGuid();
        var customer = CustomerAggregate.Create(new CustomerCreated(id, "Before"));

        var events = customer.Rename("After");

        Assert.AreEqual("Before", customer.Name);
        Assert.HasCount(1, events);
        Assert.IsInstanceOfType<CustomerRenamed>(events[0]);
        Assert.AreEqual("After", ((CustomerRenamed)events[0]).Name);
    }

    [TestMethod]
    public void Inactive_customer_rejects_rename()
    {
        var customer = CustomerAggregate.Apply(
            new CustomerDeactivated(),
            CustomerAggregate.Create(new CustomerCreated(Guid.NewGuid(), "Customer")));

        Assert.ThrowsExactly<BusinessRuleException>(() => customer.Rename("Nope"));
    }

    [TestMethod]
    public void Tenant_owner_cannot_be_removed_or_demoted()
    {
        var ownerId = Guid.NewGuid();
        var tenant = TenantAggregate.Create(new TenantCreated(Guid.NewGuid(), "Acme", ownerId));

        Assert.ThrowsExactly<BusinessRuleException>(() => tenant.RemoveMember(ownerId));
        Assert.ThrowsExactly<BusinessRuleException>(() => tenant.ChangeRole(ownerId, TenantRoles.Member));
    }
}
