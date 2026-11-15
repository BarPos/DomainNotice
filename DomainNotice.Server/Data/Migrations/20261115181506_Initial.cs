using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DomainNotice.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Announcements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BodyMarkdown = table.Column<string>(type: "text", nullable: false),
                    MinDismissSeconds = table.Column<int>(type: "integer", nullable: false),
                    UnlockMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AnonymousAnswers = table.Column<bool>(type: "boolean", nullable: false),
                    StartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MaxDisplays = table.Column<int>(type: "integer", nullable: false),
                    RepeatIntervalMinutes = table.Column<int>(type: "integer", nullable: false),
                    IsDraft = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Announcements", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Actor = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TargetType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TargetId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Details = table.Column<string>(type: "text", nullable: true),
                    RemoteIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Computers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    DnsName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    AgentKeyFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EnrolledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Online = table.Column<bool>(type: "boolean", nullable: false),
                    AgentVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OsVersion = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    RemoteIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LocalIps = table.Column<List<string>>(type: "text[]", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    DirectoryObjectGuid = table.Column<Guid>(type: "uuid", nullable: true),
                    AdLinkState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AdLinkedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReplacedByComputerId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReplacedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConflictsWithComputerId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Computers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DirectoryMemberships",
                columns: table => new
                {
                    GroupGuid = table.Column<Guid>(type: "uuid", nullable: false),
                    MemberGuid = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectoryMemberships", x => new { x.GroupGuid, x.MemberGuid });
                });

            migrationBuilder.CreateTable(
                name: "DirectoryObjects",
                columns: table => new
                {
                    ObjectGuid = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DistinguishedName = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    DistinguishedNameLower = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ParentGuid = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    SamAccountName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SamAccountNameLower = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    UserPrincipalName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    DnsHostName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ObjectSid = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    UsnChanged = table.Column<long>(type: "bigint", nullable: false),
                    LastSyncedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectoryObjects", x => x.ObjectGuid);
                });

            migrationBuilder.CreateTable(
                name: "DirectorySyncRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    Host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ObjectsChanged = table.Column<int>(type: "integer", nullable: false),
                    HighestUsn = table.Column<long>(type: "bigint", nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectorySyncRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DirectoryTransitiveMemberships",
                columns: table => new
                {
                    GroupGuid = table.Column<Guid>(type: "uuid", nullable: false),
                    MemberGuid = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectoryTransitiveMemberships", x => new { x.GroupGuid, x.MemberGuid });
                });

            migrationBuilder.CreateTable(
                name: "LocalAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    NormalizedUserName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Disabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    LastSignInAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PasswordChangedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServerUpdateRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FromVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ToVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RequestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerUpdateRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true),
                    IsSecret = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "AnnouncementButtons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AnnouncementId = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Hidden = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnnouncementButtons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnnouncementButtons_Announcements_AnnouncementId",
                        column: x => x.AnnouncementId,
                        principalTable: "Announcements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AnnouncementQuestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AnnouncementId = table.Column<Guid>(type: "uuid", nullable: false),
                    Prompt = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Required = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Hidden = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnnouncementQuestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnnouncementQuestions_Announcements_AnnouncementId",
                        column: x => x.AnnouncementId,
                        principalTable: "Announcements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AnnouncementTargets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AnnouncementId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DirectoryObjectGuid = table.Column<Guid>(type: "uuid", nullable: true),
                    ComputerId = table.Column<Guid>(type: "uuid", nullable: true),
                    Value = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnnouncementTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnnouncementTargets_Announcements_AnnouncementId",
                        column: x => x.AnnouncementId,
                        principalTable: "Announcements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentUpdateTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ComputerId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ToVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BytesSent = table.Column<long>(type: "bigint", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    LogTail = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentUpdateTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentUpdateTasks_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ComputerIpHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComputerIpHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComputerIpHistory_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ComputerNameHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComputerNameHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComputerNameHistory_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ComputerSessions",
                columns: table => new
                {
                    ComputerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<int>(type: "integer", nullable: false),
                    UserName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    UserKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LogonAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComputerSessions", x => new { x.ComputerId, x.SessionId });
                    table.ForeignKey(
                        name: "FK_ComputerSessions_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Deliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AnnouncementId = table.Column<Guid>(type: "uuid", nullable: false),
                    ComputerId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    UserKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ShownAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConfirmedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    WithdrawnAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Deliveries_Announcements_AnnouncementId",
                        column: x => x.AnnouncementId,
                        principalTable: "Announcements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Deliveries_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserLogonHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<int>(type: "integer", nullable: false),
                    UserName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    UserKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    LogonAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LogoffAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserLogonHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserLogonHistory_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QuestionOptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    QuestionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Hidden = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestionOptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuestionOptions_AnnouncementQuestions_QuestionId",
                        column: x => x.QuestionId,
                        principalTable: "AnnouncementQuestions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Confirmations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AnnouncementId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeliveryId = table.Column<Guid>(type: "uuid", nullable: true),
                    ComputerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ComputerName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    UserName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    UserKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ButtonId = table.Column<Guid>(type: "uuid", nullable: false),
                    ButtonLabel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ConfirmedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Confirmations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Confirmations_Announcements_AnnouncementId",
                        column: x => x.AnnouncementId,
                        principalTable: "Announcements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Confirmations_Deliveries_DeliveryId",
                        column: x => x.DeliveryId,
                        principalTable: "Deliveries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AnswerSets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AnnouncementId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConfirmationId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnswerSets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnswerSets_Announcements_AnnouncementId",
                        column: x => x.AnnouncementId,
                        principalTable: "Announcements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AnswerSets_Confirmations_ConfirmationId",
                        column: x => x.ConfirmationId,
                        principalTable: "Confirmations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Answers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AnswerSetId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuestionId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuestionPrompt = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Answers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Answers_AnswerSets_AnswerSetId",
                        column: x => x.AnswerSetId,
                        principalTable: "AnswerSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AnswerSelections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AnswerId = table.Column<Guid>(type: "uuid", nullable: false),
                    OptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    OptionLabel = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnswerSelections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnswerSelections_Answers_AnswerId",
                        column: x => x.AnswerId,
                        principalTable: "Answers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentUpdateTasks_ComputerId_State",
                table: "AgentUpdateTasks",
                columns: new[] { "ComputerId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentUpdateTasks_CreatedAtUtc",
                table: "AgentUpdateTasks",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AnnouncementButtons_AnnouncementId",
                table: "AnnouncementButtons",
                column: "AnnouncementId");

            migrationBuilder.CreateIndex(
                name: "IX_AnnouncementQuestions_AnnouncementId",
                table: "AnnouncementQuestions",
                column: "AnnouncementId");

            migrationBuilder.CreateIndex(
                name: "IX_Announcements_StartUtc_EndUtc",
                table: "Announcements",
                columns: new[] { "StartUtc", "EndUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AnnouncementTargets_AnnouncementId",
                table: "AnnouncementTargets",
                column: "AnnouncementId");

            migrationBuilder.CreateIndex(
                name: "IX_AnnouncementTargets_ComputerId",
                table: "AnnouncementTargets",
                column: "ComputerId");

            migrationBuilder.CreateIndex(
                name: "IX_AnnouncementTargets_DirectoryObjectGuid",
                table: "AnnouncementTargets",
                column: "DirectoryObjectGuid");

            migrationBuilder.CreateIndex(
                name: "IX_Answers_AnswerSetId",
                table: "Answers",
                column: "AnswerSetId");

            migrationBuilder.CreateIndex(
                name: "IX_Answers_QuestionId",
                table: "Answers",
                column: "QuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_AnswerSelections_AnswerId",
                table: "AnswerSelections",
                column: "AnswerId");

            migrationBuilder.CreateIndex(
                name: "IX_AnswerSelections_OptionId",
                table: "AnswerSelections",
                column: "OptionId");

            migrationBuilder.CreateIndex(
                name: "IX_AnswerSets_AnnouncementId",
                table: "AnswerSets",
                column: "AnnouncementId");

            migrationBuilder.CreateIndex(
                name: "IX_AnswerSets_ConfirmationId",
                table: "AnswerSets",
                column: "ConfirmationId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_AtUtc",
                table: "AuditLog",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ComputerIpHistory_Address",
                table: "ComputerIpHistory",
                column: "Address");

            migrationBuilder.CreateIndex(
                name: "IX_ComputerIpHistory_ComputerId_IsCurrent",
                table: "ComputerIpHistory",
                columns: new[] { "ComputerId", "IsCurrent" });

            migrationBuilder.CreateIndex(
                name: "IX_ComputerIpHistory_LastSeenUtc",
                table: "ComputerIpHistory",
                column: "LastSeenUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ComputerNameHistory_ComputerId_IsCurrent",
                table: "ComputerNameHistory",
                columns: new[] { "ComputerId", "IsCurrent" });

            migrationBuilder.CreateIndex(
                name: "IX_ComputerNameHistory_LastSeenUtc",
                table: "ComputerNameHistory",
                column: "LastSeenUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Computers_AdLinkState",
                table: "Computers",
                column: "AdLinkState");

            migrationBuilder.CreateIndex(
                name: "IX_Computers_AgentKeyFingerprint",
                table: "Computers",
                column: "AgentKeyFingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Computers_DirectoryObjectGuid",
                table: "Computers",
                column: "DirectoryObjectGuid");

            migrationBuilder.CreateIndex(
                name: "IX_Computers_Name",
                table: "Computers",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_ComputerSessions_UserKey",
                table: "ComputerSessions",
                column: "UserKey");

            migrationBuilder.CreateIndex(
                name: "IX_Confirmations_AnnouncementId_UserKey",
                table: "Confirmations",
                columns: new[] { "AnnouncementId", "UserKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Confirmations_ComputerId",
                table: "Confirmations",
                column: "ComputerId");

            migrationBuilder.CreateIndex(
                name: "IX_Confirmations_ConfirmedAtUtc",
                table: "Confirmations",
                column: "ConfirmedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Confirmations_DeliveryId",
                table: "Confirmations",
                column: "DeliveryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_AnnouncementId_UserKey",
                table: "Deliveries",
                columns: new[] { "AnnouncementId", "UserKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_ComputerId_UserKey",
                table: "Deliveries",
                columns: new[] { "ComputerId", "UserKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_SentAtUtc",
                table: "Deliveries",
                column: "SentAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryMemberships_MemberGuid",
                table: "DirectoryMemberships",
                column: "MemberGuid");

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryObjects_DistinguishedNameLower",
                table: "DirectoryObjects",
                column: "DistinguishedNameLower");

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryObjects_DnsHostName",
                table: "DirectoryObjects",
                column: "DnsHostName");

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryObjects_Kind_SamAccountNameLower",
                table: "DirectoryObjects",
                columns: new[] { "Kind", "SamAccountNameLower" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryObjects_ParentGuid",
                table: "DirectoryObjects",
                column: "ParentGuid");

            migrationBuilder.CreateIndex(
                name: "IX_DirectorySyncRuns_StartedAtUtc",
                table: "DirectorySyncRuns",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryTransitiveMemberships_MemberGuid",
                table: "DirectoryTransitiveMemberships",
                column: "MemberGuid");

            migrationBuilder.CreateIndex(
                name: "IX_LocalAccounts_NormalizedUserName",
                table: "LocalAccounts",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuestionOptions_QuestionId",
                table: "QuestionOptions",
                column: "QuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_UserLogonHistory_ComputerId_LogoffAtUtc",
                table: "UserLogonHistory",
                columns: new[] { "ComputerId", "LogoffAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_UserLogonHistory_LogoffAtUtc",
                table: "UserLogonHistory",
                column: "LogoffAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_UserLogonHistory_UserKey",
                table: "UserLogonHistory",
                column: "UserKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentUpdateTasks");

            migrationBuilder.DropTable(
                name: "AnnouncementButtons");

            migrationBuilder.DropTable(
                name: "AnnouncementTargets");

            migrationBuilder.DropTable(
                name: "AnswerSelections");

            migrationBuilder.DropTable(
                name: "AuditLog");

            migrationBuilder.DropTable(
                name: "ComputerIpHistory");

            migrationBuilder.DropTable(
                name: "ComputerNameHistory");

            migrationBuilder.DropTable(
                name: "ComputerSessions");

            migrationBuilder.DropTable(
                name: "DirectoryMemberships");

            migrationBuilder.DropTable(
                name: "DirectoryObjects");

            migrationBuilder.DropTable(
                name: "DirectorySyncRuns");

            migrationBuilder.DropTable(
                name: "DirectoryTransitiveMemberships");

            migrationBuilder.DropTable(
                name: "LocalAccounts");

            migrationBuilder.DropTable(
                name: "QuestionOptions");

            migrationBuilder.DropTable(
                name: "ServerUpdateRuns");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "UserLogonHistory");

            migrationBuilder.DropTable(
                name: "Answers");

            migrationBuilder.DropTable(
                name: "AnnouncementQuestions");

            migrationBuilder.DropTable(
                name: "AnswerSets");

            migrationBuilder.DropTable(
                name: "Confirmations");

            migrationBuilder.DropTable(
                name: "Deliveries");

            migrationBuilder.DropTable(
                name: "Announcements");

            migrationBuilder.DropTable(
                name: "Computers");
        }
    }
}
