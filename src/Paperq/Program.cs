using Paperq;

var io = new CliIo(
    Console.In,
    Console.Out,
    Console.Error,
    !Console.IsInputRedirected && !Console.IsOutputRedirected);

return CliApplication.Run(args, io);

