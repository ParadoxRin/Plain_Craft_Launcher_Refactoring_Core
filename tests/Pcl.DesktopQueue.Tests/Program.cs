await Pcl.Desktop.IconRequestQueueChecks.RunAsync();
Console.WriteLine("PASS: thumbnail burst admits at most 4 requests; page cancellation prevents queued requests; failed requests release slots; shutdown drains all work.");
