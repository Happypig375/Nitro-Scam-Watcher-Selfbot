#r "nuget: Ical.NET"
#r "nuget: Discord.NET"
let identifiableLocation = "International F# online event, see description"
task {
    printfn "Started."
    let calendarEvents =
        use http = new System.Net.Http.HttpClient()
        // https://sergeytihon.com/f-events/
        use stream = http.GetStreamAsync "https://calendar.google.com/calendar/ical/retcpic7o1iggr3cmqio8lcu8k%40group.calendar.google.com/public/basic.ics" |> fun x -> x.Result
        Ical.Net.Calendar.Load(stream).Events
    use client = new Discord.WebSocket.DiscordSocketClient()
    client.add_Log(fun msg -> task { printfn $"{msg}" })
    do! client.LoginAsync(Discord.TokenType.Bot, System.Environment.GetEnvironmentVariable "BOT_LOGIN_TOKEN")
    do! client.StartAsync()

    let completion = System.Threading.Tasks.TaskCompletionSource()
    client.add_Ready(fun () -> task {
        printfn "Ready. Processing started."
        let utcNowOffset = System.DateTimeOffset.UtcNow
        let utcNow = utcNowOffset.UtcDateTime
        for guild in client.Guilds do
            let existingDiscordEvents = System.Linq.Enumerable.ToDictionary (guild.Events |> Seq.filter (fun e -> e.Location = identifiableLocation), fun e -> e.Name)
            for e in calendarEvents do
                if e.DtStart.AsUtc > utcNow then // Don't add new already started events
                    try
                        match (existingDiscordEvents.Remove: _ -> _ * _) e.Summary with
                        | false, _ ->
                            let! _ = guild.CreateEventAsync(e.Summary, e.DtStart.AsDateTimeOffset, Discord.GuildScheduledEventType.External,
                                 Discord.GuildScheduledEventPrivacyLevel.Private, e.Description, e.DtEnd.AsDateTimeOffset, System.Nullable(), identifiableLocation)
                            printfn $"Created scheduled event '{e.Summary}' for '{guild}'."
                        | true, existingDiscordEvent ->
                            do! existingDiscordEvent.ModifyAsync(fun props ->
                                props.Name <- e.Summary
                                props.StartTime <- e.DtStart.AsDateTimeOffset
                                props.Type <- Discord.GuildScheduledEventType.External
                                props.PrivacyLevel <- Discord.GuildScheduledEventPrivacyLevel.Private
                                props.Description <- e.Description
                                props.EndTime <- e.DtEnd.AsDateTimeOffset
                                props.ChannelId <- Discord.Optional.Create(System.Nullable())
                                props.Location <- identifiableLocation
                            )
                            printfn $"Modified scheduled event '{e.Summary}' for '{guild}'."
                    with exn -> printfn $"Error processing scheduled event '{e.Summary}' for '{guild}'.\n{exn}"
            for remainingDiscordEvent in existingDiscordEvents.Values do
                if remainingDiscordEvent.StartTime > utcNowOffset then // Don't remove already started events
                    do! remainingDiscordEvent.DeleteAsync()
                    printfn $"Removed scheduled event '{remainingDiscordEvent.Name}' for '{guild}'."
        printfn "Processing finished."
        completion.TrySetResult() |> ignore
    })
    use cancel = new System.Threading.CancellationTokenSource(System.TimeSpan.FromMinutes 5.)
    cancel.Token.Register((fun () ->
        if completion.TrySetCanceled() then printfn "Cancelled processing due to not being ready after timeout."
    ), false) |> ignore
    do! completion.Task

    do! client.StopAsync()
    do! client.LogoutAsync()

    printfn "Finished."
} |> fun t -> t.Wait()
