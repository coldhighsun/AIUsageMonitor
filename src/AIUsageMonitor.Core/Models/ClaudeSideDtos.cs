namespace AIUsageMonitor.Core.Models;

/// <summary>
/// A Claude Code process that is (or recently was) running, from its file under the <c>sessions</c> directory.
/// </summary>
/// <param name="Pid">The process id.</param>
/// <param name="SessionId">The session the process is working on.</param>
/// <param name="Cwd">The working directory of the process.</param>
/// <param name="Name">The name of the session, if set.</param>
/// <param name="Status">The status Claude Code last reported (e.g. <c>busy</c> or <c>idle</c>).</param>
/// <param name="Kind">The kind of session (e.g. <c>interactive</c>).</param>
/// <param name="Entrypoint">The entry point (e.g. desktop app or CLI).</param>
/// <param name="Version">The Claude Code version.</param>
/// <param name="StartedAt">When the process started.</param>
/// <param name="UpdatedAt">When the file was last updated by the process.</param>
/// <param name="IsRunning"><see langword="true"/> when a process with that id exists now.</param>
public sealed record ActiveSession(
    int Pid,
    string? SessionId,
    string? Cwd,
    string? Name,
    string? Status,
    string? Kind,
    string? Entrypoint,
    string? Version,
    DateTimeOffset? StartedAt,
    DateTimeOffset? UpdatedAt,
    bool IsRunning);

/// <summary>
/// What the files next to a sub-agent's transcript say about it. The task description is deliberately not read.
/// </summary>
/// <param name="AgentId">The sub-agent id, taken from its file name.</param>
/// <param name="SessionId">The session that spawned it, taken from the folder name.</param>
/// <param name="AgentType">The type of agent (e.g. <c>Explore</c>, <c>Plan</c>).</param>
/// <param name="SpawnDepth">How deeply nested the agent is.</param>
/// <param name="Model">The model the agent's requests used.</param>
/// <param name="FastMode">Whether the agent ran in fast mode.</param>
/// <param name="OneHourCache">Whether the agent used the 1-hour prompt-cache lifetime.</param>
/// <param name="Effort">The reasoning effort of the agent.</param>
/// <param name="ThinkingMode">The thinking mode of the agent.</param>
/// <param name="CallCount">How many API calls the agent made.</param>
public sealed record SubagentInfo(
    string AgentId,
    string? SessionId,
    string? AgentType,
    int? SpawnDepth,
    string? Model,
    bool? FastMode,
    bool? OneHourCache,
    string? Effort,
    string? ThinkingMode,
    long? CallCount);

/// <summary>
/// What data the local Claude Code folder holds, so a front end can tell what it can show. Only file metadata is read.
/// </summary>
/// <param name="ProjectFolders">The number of project folders.</param>
/// <param name="TranscriptFiles">The number of transcripts, sub-agent transcripts included.</param>
/// <param name="SubagentTranscriptFiles">The number of those that belong to sub-agents.</param>
/// <param name="TranscriptBytes">The total size of the transcripts.</param>
/// <param name="OldestTranscriptWrite">When the least recently written transcript was last written.</param>
/// <param name="NewestTranscriptWrite">When the most recently written transcript was last written.</param>
/// <param name="PlanFiles">The number of saved plan documents.</param>
/// <param name="FileHistoryFiles">The number of file backups.</param>
/// <param name="FileHistoryBytes">The total size of the file backups.</param>
/// <param name="ActiveSessionFiles">The number of files under the <c>sessions</c> directory.</param>
/// <param name="HasStatsCache">Whether Claude Code's <c>stats-cache.json</c> exists.</param>
/// <param name="HasHistory">Whether <c>history.jsonl</c> exists.</param>
public sealed record ClaudeDataInventory(
    int ProjectFolders,
    int TranscriptFiles,
    int SubagentTranscriptFiles,
    long TranscriptBytes,
    DateTimeOffset? OldestTranscriptWrite,
    DateTimeOffset? NewestTranscriptWrite,
    int PlanFiles,
    int FileHistoryFiles,
    long FileHistoryBytes,
    int ActiveSessionFiles,
    bool HasStatsCache,
    bool HasHistory);
