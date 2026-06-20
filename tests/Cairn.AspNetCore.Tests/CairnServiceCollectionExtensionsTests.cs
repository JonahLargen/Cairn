using Cairn.AspNetCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cairn.AspNetCore.Tests;

public class CairnServiceCollectionExtensionsTests
{
    [Fact]
    public void AddCairn_returns_the_same_service_collection_for_chaining()
    {
        var services = new ServiceCollection();

        var result = services.AddCairn();

        Assert.Same(services, result);
    }

    [Fact]
    public void AddCairn_throws_when_services_is_null()
    {
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddCairn());
    }
}
