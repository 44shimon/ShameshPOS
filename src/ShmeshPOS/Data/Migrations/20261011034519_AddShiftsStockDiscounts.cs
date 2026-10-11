using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShmeshPOS.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddShiftsStockDiscounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "discount",
                table: "sales",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "discount",
                table: "sale_items",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "sale_discount_share",
                table: "sale_items",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "reorder_level",
                table: "products",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "stock_quantity",
                table: "products",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "track_stock",
                table: "products",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "sale_discount_kind",
                table: "held_sales",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "sale_discount_value",
                table: "held_sales",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "discount_kind",
                table: "held_sale_items",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "discount_value",
                table: "held_sale_items",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "shifts",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    terminal_name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    opened_by_id = table.Column<int>(type: "integer", nullable: false),
                    opened_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    opening_float = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    closed_by_id = table.Column<int>(type: "integer", nullable: true),
                    closed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    expected_cash = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    counted_cash = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shifts", x => x.id);
                    table.CheckConstraint("ck_shifts_closed_complete", "(closed_at IS NULL) = (counted_cash IS NULL)");
                    table.CheckConstraint("ck_shifts_counted_cash_non_negative", "counted_cash IS NULL OR counted_cash >= 0");
                    table.CheckConstraint("ck_shifts_opening_float_non_negative", "opening_float >= 0");
                    table.ForeignKey(
                        name: "fk_shifts_users_closed_by_id",
                        column: x => x.closed_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shifts_users_opened_by_id",
                        column: x => x.opened_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_movements",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    product_id = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    change = table.Column<int>(type: "integer", nullable: false),
                    quantity_after = table.Column<int>(type: "integer", nullable: false),
                    sale_id = table.Column<long>(type: "bigint", nullable: true),
                    refund_id = table.Column<long>(type: "bigint", nullable: true),
                    note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    terminal_name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_movements", x => x.id);
                    table.ForeignKey(
                        name: "fk_stock_movements_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_movements_refunds_refund_id",
                        column: x => x.refund_id,
                        principalTable: "refunds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_movements_sales_sale_id",
                        column: x => x.sale_id,
                        principalTable: "sales",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_movements_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_sales_discount_non_negative",
                table: "sales",
                sql: "discount >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sale_items_discounts",
                table: "sale_items",
                sql: "discount >= 0 AND sale_discount_share >= 0 AND line_total >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_products_reorder_level_non_negative",
                table: "products",
                sql: "reorder_level IS NULL OR reorder_level >= 0");

            migrationBuilder.CreateIndex(
                name: "ix_shifts_closed_by_id",
                table: "shifts",
                column: "closed_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_shifts_one_open_per_terminal",
                table: "shifts",
                column: "terminal_name",
                unique: true,
                filter: "closed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_shifts_opened_at",
                table: "shifts",
                column: "opened_at");

            migrationBuilder.CreateIndex(
                name: "ix_shifts_opened_by_id",
                table: "shifts",
                column: "opened_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_product_id_created_at",
                table: "stock_movements",
                columns: new[] { "product_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_refund_id",
                table: "stock_movements",
                column: "refund_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_sale_id",
                table: "stock_movements",
                column: "sale_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_user_id",
                table: "stock_movements",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "shifts");

            migrationBuilder.DropTable(
                name: "stock_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sales_discount_non_negative",
                table: "sales");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sale_items_discounts",
                table: "sale_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_products_reorder_level_non_negative",
                table: "products");

            migrationBuilder.DropColumn(
                name: "discount",
                table: "sales");

            migrationBuilder.DropColumn(
                name: "discount",
                table: "sale_items");

            migrationBuilder.DropColumn(
                name: "sale_discount_share",
                table: "sale_items");

            migrationBuilder.DropColumn(
                name: "reorder_level",
                table: "products");

            migrationBuilder.DropColumn(
                name: "stock_quantity",
                table: "products");

            migrationBuilder.DropColumn(
                name: "track_stock",
                table: "products");

            migrationBuilder.DropColumn(
                name: "sale_discount_kind",
                table: "held_sales");

            migrationBuilder.DropColumn(
                name: "sale_discount_value",
                table: "held_sales");

            migrationBuilder.DropColumn(
                name: "discount_kind",
                table: "held_sale_items");

            migrationBuilder.DropColumn(
                name: "discount_value",
                table: "held_sale_items");
        }
    }
}
