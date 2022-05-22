#r "nuget: System.Threading.RateLimiting, 7.0.0-preview.4.22229.4"
#r "nuget: Leaf.xNet, 5.2.10"
#r "nuget: Newtonsoft.Json, 13.0.1"
#r "nuget: System.Drawing.Common, 7.0.0-preview.2.22152.2"
#r "nuget: websocketsharp.core, 1.0.0"
#r @"C:\Users\hadri\Source\Repos\Anarchy\Anarchy\bin\Debug\net6.0\Anarchy.dll"
open Discord
open Discord.Gateway
let env = System.Environment.GetEnvironmentVariable
let token = env "TOKEN" // https://discordhelp.net/discord-token
let commandGuildId = uint64 (env "COMMAND_GUILD_ID")
let commandChannelId = uint64 (env "COMMAND_CHANNEL_ID") // channel in commandGuildId
let logChannelId = uint64 (env "LOG_CHANNEL_ID") // channel in commandGuildId
let whitelistedGuildId = uint64 (env "WHITELISTED_GUILD_ID")
(task {
    let completion = System.Threading.Tasks.TaskCompletionSource()
    let limiter = new System.Threading.RateLimiting.FixedWindowRateLimiter(
        System.Threading.RateLimiting.FixedWindowRateLimiterOptions(
            2, enum 0, 0, System.TimeSpan.FromSeconds 1.
        )
    )
    let loggedInMsg = "Logged in"
    let http = new System.Net.Http.HttpClient()
    let send obj = // https://birdie0.github.io/discord-webhooks-guide
        task {
            let json = System.Net.Http.Json.JsonContent.Create obj
            let! _ = http.PostAsync(env "WEBHOOK1", json)
            let! _ = http.PostAsync(env "WEBHOOK2", json)
            ()
        }
    let log, error =
        let log (color: int) (message: string) =
            printfn $"{message}"
            send {|
                embeds = [
                    {|
                        color = color
                        description = message
                    |}
                ]
            |}
        log 1127128, log 16711680
    try
        let client = new DiscordSocketClient()
        client.Login token
        let mutable finishedInit = false
        let handler (client: Discord.Gateway.DiscordSocketClient) (args: Discord.Gateway.MessageEventArgs) =
            task {
                try
                    if args.Message.Guild <> null && args.Message.Guild.Id = commandGuildId then
                        let stop() = task {
                            do! args.Message.AddReactionAsync "✅"
                            client.Logout()
                            http.Dispose()
                            client.Dispose()
                            limiter.Dispose()
                            completion.SetResult()
                            exit 0
                        }
                        if args.Message.Channel.Id = logChannelId then
                            if args.Message.Embed <> null && args.Message.Embed.Description = loggedInMsg then
                                if finishedInit then do! stop() else finishedInit <- true
                        elif args.Message.Channel.Id = commandChannelId then
                            match args.Message.Content with
                            | "stop" -> do! stop()
                            | "export" ->
                                do! log "Exporting guilds..."
                                let! guilds = client.GetGuildsAsync()
                                for guild in guilds |> Seq.filter (fun g -> g.Id <> commandGuildId && g.Id <> whitelistedGuildId) do // |> Seq.take 1 do
                                    let! guild = client.GetGuildAsync guild.Id
                                    let! owner = guild.GetMemberAsync guild.OwnerId
                                    //let! channels = guild.GetChannelsAsync()
                                    //let firstChannel = channels[0].Id
                                    //let! roles = guild.GetRolesAsync()
                                    //let roles = roles |> Seq.map (fun role -> role.Id, role) |> Map
                                    //let getMaxRole (mem: GuildMember) = mem.Roles |> Seq.map (fun role -> roles[role]) |> Seq.maxBy (fun role -> role.Position)
                                    //let! me = guild.GetMemberAsync client.User.Id
                                    //let isNotMyRoleLevel = if me.Roles.Count > 0 then (<>) (getMaxRole me) else fun _ -> true
                                    //do! log $"Getting guild members of {guild.Name} ({guild.Id})..."
                                    //let! channelMembers = client.GetGuildChannelMembersAsync(guild.Id, firstChannel, 100u) // Not setting a limit takes too long to load
                                    //do! log $"Got members of {guild.Name} ({guild.Id})..."
                                    let vanity = if isNull guild.VanityInvite then "" else $"\nVanity Invite: {guild.VanityInvite}"
                                    do! send {|
                                        username = guild.Name
                                        avatar_url = if isNull guild.Icon then null else guild.Icon.Url
                                        content = $"Guild ID: {guild.Id}\nGuild Owner: {owner} ({owner.User.Id}){vanity}"
                                        //embeds = [{|
                                        //    fields =
                                        //        channelMembers
                                        //        |> Seq.filter (fun mem ->
                                        //            match mem.User.Type with
                                        //            | DiscordUserType.User -> true // Get users with special roles
                                        //            | DiscordUserType.Bot -> not (mem.User.Badges.HasFlag DiscordBadge.VerifiedBot) // Get unverified bots
                                        //            | _ -> false
                                        //            && mem.Roles.Count > 0
                                        //        )
                                        //        |> Seq.groupBy getMaxRole
                                        //        |> Seq.filter (fst >> isNotMyRoleLevel)
                                        //        |> Seq.sortByDescending (fun (role, _) -> role.Position)
                                        //        |> Seq.map (fun (role, members) ->
                                        //            {|
                                        //                name = $"Role: {role.Name}"
                                        //                value =
                                        //                    members 
                                        //                    |> Seq.map (fun mem -> $"""{mem.User} ({mem.User.Id}{if mem.User.Type = DiscordUserType.Bot then ", Unverified Bot" else ""})""")
                                        //                    |> String.concat "\n"
                                        //            |}
                                        //        )
                                        //|}]
                                    |}
                            | _ -> do! args.Message.AddReactionAsync "❓"
                    else
                    let sender = args.Message.Author.User
                    if sender.Type <> DiscordUserType.User then () else // Ignore bots and webhooks
                    let contains: string -> _ = if isNull args.Message.Content then (fun _ -> true) else args.Message.Content.Contains
                    if (contains "discord" || contains ".com" || contains ".gg" || contains "http" || args.Message.Attachment <> null) // Detect invites and files
                        && not (contains "discord.gift/") // Don't spam claimed nitro links
                        && not (contains "tenor.com/") // Don't spam gifs
                        && limiter.Acquire(1).IsAcquired then // Rate limit to prevent spamming
                        let msg = {|
                            username = sender.Username
                            content = args.Message.Content
                            allowed_mentions = {| parse = [] |} // Ping no one
                            embeds = [
                                let attachment = args.Message.Attachment
                                if attachment <> null then
                                    let size, unit = float attachment.FileSize, "bytes"
                                    let size, unit = if size > 1024. then size / 1024., "KiB" else size, unit
                                    let size, unit = if size > 1024. then size / 1024., "MiB" else size, unit
                                    box {| name = $"Attached File ({attachment.FileSize} bytes, or {System.Math.Round(size, 2)} {unit})"; value = $"[attachment.FileName]({attachment.Url})" |}
                            ]
                        |}
                        match args.Message.Guild, sender.Avatar with
                        | null, null -> do! send {| msg with embeds = (box {| description = $"Sender ID: {sender.Id} ({sender})\n(Direct Message)" |})::msg.embeds |}
                        | null, avatar -> do! send {| msg with embeds = (box {| description = $"Sender ID: {sender.Id} ({sender})\n(Direct Message)" |})::msg.embeds; avatar_url = avatar.Url |}
                        | guild, null ->
                            let! guild = client.GetGuildAsync guild.Id
                            let! owner = client.GetUserAsync guild.OwnerId
                            let embed = {|
                                description = $"Sender: {sender} ({sender.Id})\nGuild: {guild.Name} ({guild.Id})\nGuild Owner: {owner} ({guild.OwnerId})\n[Jump to Message](https://discord.com/channels/{guild.Id}/{args.Message.Channel.Id}/{args.Message.Id})"
                            |}
                            do! send {| msg with embeds = (if guild.Icon <> null then box {| embed with thumbnail = {| url = guild.Icon.Url |} |} else embed)::msg.embeds |}
                        | guild, avatar ->
                            let! guild = client.GetGuildAsync guild.Id
                            let! owner = client.GetUserAsync guild.OwnerId
                            let embed = {|
                                description = $"Sender: {sender} ({sender.Id})\nGuild: {guild.Name} ({guild.Id})\nGuild Owner: {owner} ({guild.OwnerId})\n[Jump to Message](https://discord.com/channels/{guild.Id}/{args.Message.Channel.Id}/{args.Message.Id})"
                            |}
                            do! send {| msg with embeds = (if guild.Icon <> null then box {| embed with thumbnail = {| url = guild.Icon.Url |} |} else embed)::msg.embeds; avatar_url = avatar.Url |}
                with e -> do! error (string e)
            } |> ignore
        client.add_OnMessageReceived handler
        client.add_OnMessageEdited handler
        do! log loggedInMsg
        do! completion.Task
    with e -> do! error (string e)
}).Wait()