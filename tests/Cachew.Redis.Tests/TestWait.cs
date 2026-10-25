using Xunit.Sdk;

namespace Cachew.Redis.Tests;

/// <summary>Polls a condition, because pub/sub delivery and indexing are asynchronous.</summary>
internal static class TestWait
{
    public static async Task Eventually(Func<Task<bool>> condition, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new XunitException("The condition was not satisfied in time.");
    }
}
