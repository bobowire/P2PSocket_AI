using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace P2P.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ts = table.Column<string>(type: "TEXT", nullable: false),
                    @event = table.Column<string>(name: "event", type: "TEXT", nullable: false),
                    device_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    detail = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mappings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    owner_device_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    local_port = table.Column<int>(type: "INTEGER", nullable: false),
                    proto = table.Column<string>(type: "TEXT", nullable: false),
                    target_device_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    target_addr = table.Column<string>(type: "TEXT", nullable: false),
                    target_port = table.Column<int>(type: "INTEGER", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mappings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "server_config",
                columns: table => new
                {
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_server_config", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    username = table.Column<string>(type: "TEXT", nullable: false),
                    password_hash = table.Column<string>(type: "TEXT", nullable: false),
                    is_admin = table.Column<bool>(type: "INTEGER", nullable: false),
                    disabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mapping_stats",
                columns: table => new
                {
                    mapping_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    bytes_up = table.Column<long>(type: "INTEGER", nullable: false),
                    bytes_down = table.Column<long>(type: "INTEGER", nullable: false),
                    relay_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mapping_stats", x => x.mapping_id);
                    table.ForeignKey(
                        name: "FK_mapping_stats_mappings_mapping_id",
                        column: x => x.mapping_id,
                        principalTable: "mappings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    device_name = table.Column<string>(type: "TEXT", nullable: false),
                    os = table.Column<string>(type: "TEXT", nullable: false),
                    client_version = table.Column<string>(type: "TEXT", nullable: false),
                    mac_code = table.Column<string>(type: "TEXT", nullable: false),
                    remote_code = table.Column<string>(type: "TEXT", nullable: false),
                    virtual_ip = table.Column<string>(type: "TEXT", nullable: false),
                    static_pub_key = table.Column<byte[]>(type: "BLOB", nullable: false),
                    device_secret = table.Column<byte[]>(type: "BLOB", nullable: false),
                    last_seen_at = table.Column<string>(type: "TEXT", nullable: true),
                    disabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_devices", x => x.id);
                    table.ForeignKey(
                        name: "FK_devices_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    is_default = table.Column<bool>(type: "INTEGER", nullable: false),
                    join_policy = table.Column<string>(type: "TEXT", nullable: false),
                    invite_code = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_groups", x => x.id);
                    table.ForeignKey(
                        name: "FK_groups_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "lan_segments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    device_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    cidr = table.Column<string>(type: "TEXT", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lan_segments", x => x.id);
                    table.ForeignKey(
                        name: "FK_lan_segments_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "punch_stats",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ts = table.Column<string>(type: "TEXT", nullable: false),
                    session_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    initiator_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    target_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    proto = table.Column<string>(type: "TEXT", nullable: false),
                    concurrency = table.Column<int>(type: "INTEGER", nullable: false),
                    result = table.Column<string>(type: "TEXT", nullable: false),
                    reason = table.Column<string>(type: "TEXT", nullable: true),
                    duration_ms = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_punch_stats", x => x.id);
                    table.ForeignKey(
                        name: "FK_punch_stats_devices_initiator_id",
                        column: x => x.initiator_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_punch_stats_devices_target_id",
                        column: x => x.target_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "group_members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    group_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    device_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    approved = table.Column<bool>(type: "INTEGER", nullable: false),
                    joined_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_members", x => x.id);
                    table.ForeignKey(
                        name: "FK_group_members_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_group_members_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "join_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    group_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    device_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<string>(type: "TEXT", nullable: false),
                    handled_at = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_join_requests", x => x.id);
                    table.ForeignKey(
                        name: "FK_join_requests_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_join_requests_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_audit_ts",
                table: "audit_logs",
                column: "ts");

            migrationBuilder.CreateIndex(
                name: "IX_devices_remote_code",
                table: "devices",
                column: "remote_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_devices_mac",
                table: "devices",
                column: "mac_code");

            migrationBuilder.CreateIndex(
                name: "idx_devices_owner",
                table: "devices",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_group_members_group_id_device_id",
                table: "group_members",
                columns: new[] { "group_id", "device_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_gm_device",
                table: "group_members",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "IX_groups_invite_code",
                table: "groups",
                column: "invite_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_groups_owner_user_id",
                table: "groups",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_join_requests_device_id",
                table: "join_requests",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "IX_join_requests_group_id",
                table: "join_requests",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "IX_lan_segments_device_id_cidr",
                table: "lan_segments",
                columns: new[] { "device_id", "cidr" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mappings_owner_device_id_local_port_proto",
                table: "mappings",
                columns: new[] { "owner_device_id", "local_port", "proto" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_mappings_target",
                table: "mappings",
                column: "target_device_id");

            migrationBuilder.CreateIndex(
                name: "IX_punch_stats_initiator_id",
                table: "punch_stats",
                column: "initiator_id");

            migrationBuilder.CreateIndex(
                name: "IX_punch_stats_target_id",
                table: "punch_stats",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "idx_punch_ts",
                table: "punch_stats",
                column: "ts");

            migrationBuilder.CreateIndex(
                name: "IX_users_username",
                table: "users",
                column: "username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "group_members");

            migrationBuilder.DropTable(
                name: "join_requests");

            migrationBuilder.DropTable(
                name: "lan_segments");

            migrationBuilder.DropTable(
                name: "mapping_stats");

            migrationBuilder.DropTable(
                name: "punch_stats");

            migrationBuilder.DropTable(
                name: "server_config");

            migrationBuilder.DropTable(
                name: "groups");

            migrationBuilder.DropTable(
                name: "mappings");

            migrationBuilder.DropTable(
                name: "devices");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
