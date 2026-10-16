using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BiStudent.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Mvp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Teachers",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "Teachers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsAdmin",
                table: "Teachers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastLoginAt",
                table: "Teachers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MustChangePassword",
                table: "Teachers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SecurityStamp",
                table: "Teachers",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Comment",
                table: "AttendanceMarks",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExcuseReason",
                table: "AttendanceMarks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasDocument",
                table: "AttendanceMarks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Данные прежних версий: учётные записи остаются активными, первая получает права администратора,
            // опоздания (старый статус 1) считаются присутствием.
            migrationBuilder.Sql("UPDATE \"Teachers\" SET \"IsActive\" = 1, \"CreatedAt\" = datetime('now', 'localtime'), \"SecurityStamp\" = lower(hex(randomblob(16)));");
            migrationBuilder.Sql("UPDATE \"Teachers\" SET \"IsAdmin\" = 1 WHERE \"Id\" = (SELECT MIN(\"Id\") FROM \"Teachers\");");
            migrationBuilder.Sql("UPDATE \"AttendanceMarks\" SET \"Status\" = 0 WHERE \"Status\" = 1;");

            migrationBuilder.CreateTable(
                name: "Assessments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeacherId = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Assessments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Assessments_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Assessments_Teachers_TeacherId",
                        column: x => x.TeacherId,
                        principalTable: "Teachers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EnrollmentCodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedByTeacherId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnrollmentCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EnrollmentCodes_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EnrollmentCodes_Teachers_CreatedByTeacherId",
                        column: x => x.CreatedByTeacherId,
                        principalTable: "Teachers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FinalGrades",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeacherId = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    StudentId = table.Column<int>(type: "INTEGER", nullable: false),
                    Score = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinalGrades", x => x.Id);
                    table.CheckConstraint("CK_FinalGrades_Score", "Score BETWEEN 1 AND 100");
                    table.ForeignKey(
                        name: "FK_FinalGrades_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FinalGrades_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FinalGrades_Teachers_TeacherId",
                        column: x => x.TeacherId,
                        principalTable: "Teachers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LessonGrades",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SlotId = table.Column<int>(type: "INTEGER", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    StudentId = table.Column<int>(type: "INTEGER", nullable: false),
                    Score = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LessonGrades", x => x.Id);
                    table.CheckConstraint("CK_LessonGrades_Score", "Score BETWEEN 1 AND 100");
                    table.ForeignKey(
                        name: "FK_LessonGrades_ScheduleSlots_SlotId",
                        column: x => x.SlotId,
                        principalTable: "ScheduleSlots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LessonGrades_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssessmentGrades",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AssessmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    StudentId = table.Column<int>(type: "INTEGER", nullable: false),
                    Score = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssessmentGrades", x => x.Id);
                    table.CheckConstraint("CK_AssessmentGrades_Score", "Score BETWEEN 1 AND 100");
                    table.ForeignKey(
                        name: "FK_AssessmentGrades_Assessments_AssessmentId",
                        column: x => x.AssessmentId,
                        principalTable: "Assessments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssessmentGrades_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FaceSubmissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StudentId = table.Column<int>(type: "INTEGER", nullable: false),
                    EnrollmentCodeId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    SimilarTo = table.Column<string>(type: "TEXT", nullable: true),
                    SimilarScore = table.Column<double>(type: "REAL", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReviewedByTeacherId = table.Column<int>(type: "INTEGER", nullable: true),
                    RejectReason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FaceSubmissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FaceSubmissions_EnrollmentCodes_EnrollmentCodeId",
                        column: x => x.EnrollmentCodeId,
                        principalTable: "EnrollmentCodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_FaceSubmissions_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FaceSubmissionImages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SubmissionId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProtectedImage = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FaceSubmissionImages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FaceSubmissionImages_FaceSubmissions_SubmissionId",
                        column: x => x.SubmissionId,
                        principalTable: "FaceSubmissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssessmentGrades_AssessmentId_StudentId",
                table: "AssessmentGrades",
                columns: new[] { "AssessmentId", "StudentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssessmentGrades_StudentId",
                table: "AssessmentGrades",
                column: "StudentId");

            migrationBuilder.CreateIndex(
                name: "IX_Assessments_GroupId",
                table: "Assessments",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Assessments_TeacherId_GroupId_Subject",
                table: "Assessments",
                columns: new[] { "TeacherId", "GroupId", "Subject" });

            migrationBuilder.CreateIndex(
                name: "IX_EnrollmentCodes_Code",
                table: "EnrollmentCodes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EnrollmentCodes_CreatedByTeacherId",
                table: "EnrollmentCodes",
                column: "CreatedByTeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_EnrollmentCodes_GroupId",
                table: "EnrollmentCodes",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_FaceSubmissionImages_SubmissionId",
                table: "FaceSubmissionImages",
                column: "SubmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_FaceSubmissions_EnrollmentCodeId",
                table: "FaceSubmissions",
                column: "EnrollmentCodeId");

            migrationBuilder.CreateIndex(
                name: "IX_FaceSubmissions_Status_CreatedAt",
                table: "FaceSubmissions",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FaceSubmissions_StudentId",
                table: "FaceSubmissions",
                column: "StudentId");

            migrationBuilder.CreateIndex(
                name: "IX_FinalGrades_GroupId",
                table: "FinalGrades",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_FinalGrades_StudentId",
                table: "FinalGrades",
                column: "StudentId");

            migrationBuilder.CreateIndex(
                name: "IX_FinalGrades_TeacherId_GroupId_Subject_StudentId",
                table: "FinalGrades",
                columns: new[] { "TeacherId", "GroupId", "Subject", "StudentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LessonGrades_SlotId_Date_StudentId",
                table: "LessonGrades",
                columns: new[] { "SlotId", "Date", "StudentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LessonGrades_StudentId",
                table: "LessonGrades",
                column: "StudentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssessmentGrades");

            migrationBuilder.DropTable(
                name: "FaceSubmissionImages");

            migrationBuilder.DropTable(
                name: "FinalGrades");

            migrationBuilder.DropTable(
                name: "LessonGrades");

            migrationBuilder.DropTable(
                name: "Assessments");

            migrationBuilder.DropTable(
                name: "FaceSubmissions");

            migrationBuilder.DropTable(
                name: "EnrollmentCodes");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Teachers");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "Teachers");

            migrationBuilder.DropColumn(
                name: "IsAdmin",
                table: "Teachers");

            migrationBuilder.DropColumn(
                name: "LastLoginAt",
                table: "Teachers");

            migrationBuilder.DropColumn(
                name: "MustChangePassword",
                table: "Teachers");

            migrationBuilder.DropColumn(
                name: "SecurityStamp",
                table: "Teachers");

            migrationBuilder.DropColumn(
                name: "Comment",
                table: "AttendanceMarks");

            migrationBuilder.DropColumn(
                name: "ExcuseReason",
                table: "AttendanceMarks");

            migrationBuilder.DropColumn(
                name: "HasDocument",
                table: "AttendanceMarks");
        }
    }
}
