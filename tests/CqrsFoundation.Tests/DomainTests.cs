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

    [TestMethod]
    public void Tenant_roles_bundle_permissions_without_being_the_authorization_primitive()
    {
        Assert.IsTrue(TenantRoles.Grants(TenantRoles.Owner, TenantPermissions.CustomersWrite));
        Assert.IsTrue(TenantRoles.Grants(TenantRoles.Admin, TenantPermissions.MembersManage));
        Assert.IsTrue(TenantRoles.Grants(TenantRoles.Member, TenantPermissions.CustomersRead));
        Assert.IsTrue(TenantRoles.Grants(TenantRoles.Member, TenantPermissions.AuditRead));
        Assert.IsFalse(TenantRoles.Grants(TenantRoles.Member, TenantPermissions.CustomersWrite));
        Assert.IsFalse(TenantRoles.Grants(TenantRoles.Member, TenantPermissions.MembersManage));
    }
}
