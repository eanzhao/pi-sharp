using PiSharp.Mom;

using var cancellationTokenSource = new CancellationTokenSource();

Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cancellationTokenSource.Cancel();
};

return await new MomApplication().RunAsync(args, cancellationTokenSource.Token);
