using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LucHeart.StreamAnalyzer.Utils;

public static class ServiceProviderExtensions
{
    public static T GetOptions<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(this IServiceProvider serviceProvider) where T : class
    {
        return serviceProvider.GetRequiredService<IOptions<T>>().Value;
    }
}