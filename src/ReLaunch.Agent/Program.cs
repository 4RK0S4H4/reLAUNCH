using ReLaunch.Core;

// Headless agent: runs in the logged-in user's session and does the shell work.
Console.Error.WriteLine($"{ProductInfo.Name} agent starting");
await Task.Delay(Timeout.Infinite);
