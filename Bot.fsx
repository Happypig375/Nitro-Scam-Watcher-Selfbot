let env = System.Environment.GetEnvironmentVariable
let token = env "TOKEN" // https://discordhelp.net/discord-token
let commandGuildId = uint64 (env "COMMAND_GUILD_ID")
let commandChannelId = uint64 (env "COMMAND_CHANNEL_ID") // A channel in commandGuildId
let logChannelId = uint64 (env "LOG_CHANNEL_ID") // A channel in commandGuildId
let whitelistedGuildId = uint64 (env "WHITELISTED_GUILD_ID")
let webhook1 = env "WEBHOOK1" // A webhook in logChannelId
let webhook2 = env "WEBHOOK2" // A webhook elsewhere

#r "nuget: System.Threading.RateLimiting, 7.0.0-preview.4.22229.4"
#r "nuget: Leaf.xNet, 5.2.10"
#r "nuget: Newtonsoft.Json, 13.0.1"
#r "nuget: System.Drawing.Common, 7.0.0-preview.2.22152.2"
#r "nuget: websocketsharp.core, 1.0.0"
#r "Anarchy.dll" // Custom built from https://github.com/not-ilinked/Anarchy/pull/3302
open Discord
open Discord.Gateway
(task {
    let completion = System.Threading.Tasks.TaskCompletionSource()
    let limiter = new System.Threading.RateLimiting.FixedWindowRateLimiter(
        System.Threading.RateLimiting.FixedWindowRateLimiterOptions(
            1, System.Threading.RateLimiting.QueueProcessingOrder.NewestFirst, 3, System.TimeSpan.FromSeconds 1.
        )
    )
    let loggedInMsg = "Logged in"
    let http = new System.Net.Http.HttpClient()
    let send omitSecondWebhook obj = // https://birdie0.github.io/discord-webhooks-guide
        task {
            let json = System.Net.Http.Json.JsonContent.Create obj
            let! _ = http.PostAsync(webhook1, json)
            if not omitSecondWebhook then
                let! _ = http.PostAsync(webhook2, json)
                ()
        }
    let logLoggedIn, log, error =
        let log omitSecondWebhook (color: int) (message: string) =
            printfn $"{message}"
            send omitSecondWebhook {|
                embeds = [
                    {|
                        color = color
                        description = message
                    |}
                ]
            |}
        (fun () -> log true 1127128 loggedInMsg), log false 1127128, log false 16711680
    let send obj = send false obj
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
                                for guild in guilds do
                                    let guildId = guild.Id // 953749061616414751UL
                                    let! me = client.GetGuildMemberAsync(guildId, client.User.Id)
                                    let! channels = client.GetGuildChannelsAsync guildId
                                    let firstChannel = // The first channel visible to myself, this is not necessarily just channels[0] 
                                        channels |> Seq.find (fun c ->
                                            c.PermissionOverwrites
                                            |> Seq.tryPick (fun p ->
                                                if p.Type = PermissionOverwriteType.Member && p.AffectedId = me.User.Id
                                                   || p.Type = PermissionOverwriteType.Role && (me.Roles |> Seq.exists (fun roleId -> roleId = p.AffectedId) || p.AffectedId = guildId (*@everyone*)) then
                                                    if p.Allow.HasFlag DiscordPermission.ViewChannel then Some true
                                                    elif p.Deny.HasFlag DiscordPermission.ViewChannel then Some false
                                                    else None
                                                else None
                                            ) |> Option.defaultValue true
                                        )
                                    let! roles = client.GetGuildRolesAsync guildId
                                    let rolesMap = roles |> Seq.map (fun role -> role.Id, role) |> Map
                                    let! guild = client.GetGuildAsync guild.Id
                                    let! owner = guild.GetMemberAsync guild.OwnerId
                                    let vanity = if isNull guild.VanityInvite then "" else $"\nVanity Invite: {guild.VanityInvite}"
                                    do! send {|
                                        username = guild.Name
                                        avatar_url = if isNull guild.Icon then null else guild.Icon.Url
                                        content = $"Guild ID: {guild.Id}\nGuild Owner: {owner} ({owner.User.Id}){vanity}"
                                    |}
                                    let! members = client.GetGuildChannelMembersAsync(guildId, firstChannel.Id)
                                    let memberRoles = Array.init members.Count (fun i -> members[i], Set members[i].Roles)
                                    let myRoles = Set me.Roles
                                    for role, members in
                                        roles
                                        |> Seq.filter (myRoles.Contains >> not) // No normal roles
                                        |> Seq.map (fun r ->
                                            r, memberRoles |> Array.filter (fun (mem, roles) ->
                                                roles.Contains r &&
                                                match mem.User.Type with
                                                | DiscordUserType.User -> true // Get users with special roles
                                                | DiscordUserType.Bot -> not (mem.User.Badges.HasFlag DiscordBadge.VerifiedBot) // Get unverified bots
                                                | _ -> false)
                                        ) // Get members with role
                                        |> Seq.filter (fun (r, m) -> m.Length > 0) // Filter away roles with no members
                                        |> Seq.sortByDescending (fun (r, _) -> r.Position) do // Order by importance
                                        do! send {|
                                            username = guild.Name
                                            avatar_url = if isNull guild.Icon then null else guild.Icon.Url
                                            embeds = [
                                                {|
                                                    color = role.Color.ToArgb()
                                                    title = $"{role}"
                                                    description =
                                                        members
                                                        |> Seq.map (fun (mem, _) -> $"""    {mem.User} ({mem.User.Id}{if mem.User.Type = DiscordUserType.Bot then ", Unverified Bot" else ""})""")
                                                        |> String.concat "\n    "
                                                    footer = {|
                                                        text = $"Priority: {role.Position}, Displayed separately: {role.Seperated}"
                                                    |}
                                                |}
                                            ] 
                                        |}
                                do! log "Exported guilds successfully."
                            | _ -> do! args.Message.AddReactionAsync "❓"
                    else
                    let sender = args.Message.Author.User
                    if sender.Type <> DiscordUserType.User then () else // Ignore bots and webhooks
                    let contains: string -> _ = if isNull args.Message.Content then (fun _ -> true) else args.Message.Content.Contains
                    if (contains "discord" || contains ".com" || contains ".gg" || contains "http" || args.Message.Attachment <> null) // Detect invites and files
                        && not (contains "discord.gift/") // Don't spam claimed nitro links
                        && not (contains "tenor.com/") then // Don't spam gifs
                      use! lease = limiter.WaitAsync 1
                      if lease.IsAcquired then // Rate limit to prevent spamming
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
        do! logLoggedIn()
        do! System.Threading.Tasks.Task.Delay (System.TimeSpan.FromSeconds 20.) // Ignore any logged in messages generated by this instance
        if not finishedInit then finishedInit <- true
        do! completion.Task
    with e -> do! error (string e)
}).Wait()
