using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShmeshPOS.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHeldSalesAndRefunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "refund_id",
                table: "customer_transactions",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "held_sales",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    label = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    cashier_id = table.Column<int>(type: "integer", nullable: false),
                    customer_id = table.Column<int>(type: "integer", nullable: true),
                    terminal_name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    total = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_held_sales", x => x.id);
                    table.ForeignKey(
                        name: "fk_held_sales_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_held_sales_users_cashier_id",
                        column: x => x.cashier_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "refunds",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    refund_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    original_sale_id = table.Column<long>(type: "bigint", nullable: false),
                    cashier_id = table.Column<int>(type: "integer", nullable: false),
                    terminal_name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    payment_method_id = table.Column<int>(type: "integer", nullable: false),
                    subtotal = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    tax = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    total = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refunds", x => x.id);
                    table.CheckConstraint("ck_refunds_total", "total = subtotal + tax");
                    table.CheckConstraint("ck_refunds_total_non_negative", "total >= 0");
                    table.ForeignKey(
                        name: "fk_refunds_payment_methods_payment_method_id",
                        column: x => x.payment_method_id,
                        principalTable: "payment_methods",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refunds_sales_original_sale_id",
                        column: x => x.original_sale_id,
                        principalTable: "sales",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refunds_users_cashier_id",
                        column: x => x.cashier_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "held_sale_items",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    held_sale_id = table.Column<int>(type: "integer", nullable: false),
                    product_id = table.Column<int>(type: "integer", nullable: false),
                    product_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    tax_rate = table.Column<decimal>(type: "numeric(6,5)", precision: 6, scale: 5, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_held_sale_items", x => x.id);
                    table.CheckConstraint("ck_held_sale_items_quantity_positive", "quantity > 0");
                    table.ForeignKey(
                        name: "fk_held_sale_items_held_sales_held_sale_id",
                        column: x => x.held_sale_id,
                        principalTable: "held_sales",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_held_sale_items_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "refund_items",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    refund_id = table.Column<long>(type: "bigint", nullable: false),
                    sale_item_id = table.Column<long>(type: "bigint", nullable: false),
                    product_id = table.Column<int>(type: "integer", nullable: false),
                    product_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    tax_rate = table.Column<decimal>(type: "numeric(6,5)", precision: 6, scale: 5, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    line_total = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refund_items", x => x.id);
                    table.CheckConstraint("ck_refund_items_quantity_positive", "quantity > 0");
                    table.ForeignKey(
                        name: "fk_refund_items_refunds_refund_id",
                        column: x => x.refund_id,
                        principalTable: "refunds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_refund_items_sale_items_sale_item_id",
                        column: x => x.sale_item_id,
                        principalTable: "sale_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_customer_transactions_refund_id",
                table: "customer_transactions",
                column: "refund_id");

            migrationBuilder.CreateIndex(
                name: "ix_held_sale_items_held_sale_id",
                table: "held_sale_items",
                column: "held_sale_id");

            migrationBuilder.CreateIndex(
                name: "ix_held_sale_items_product_id",
                table: "held_sale_items",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_held_sales_cashier_id",
                table: "held_sales",
                column: "cashier_id");

            migrationBuilder.CreateIndex(
                name: "ix_held_sales_created_at",
                table: "held_sales",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_held_sales_customer_id",
                table: "held_sales",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_refund_items_refund_id",
                table: "refund_items",
                column: "refund_id");

            migrationBuilder.CreateIndex(
                name: "ix_refund_items_sale_item_id",
                table: "refund_items",
                column: "sale_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_refunds_cashier_id",
                table: "refunds",
                column: "cashier_id");

            migrationBuilder.CreateIndex(
                name: "ix_refunds_created_at",
                table: "refunds",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_refunds_original_sale_id",
                table: "refunds",
                column: "original_sale_id");

            migrationBuilder.CreateIndex(
                name: "ix_refunds_payment_method_id",
                table: "refunds",
                column: "payment_method_id");

            migrationBuilder.CreateIndex(
                name: "ix_refunds_refund_number",
                table: "refunds",
                column: "refund_number",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_customer_transactions_refunds_refund_id",
                table: "customer_transactions",
                column: "refund_id",
                principalTable: "refunds",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_customer_transactions_refunds_refund_id",
                table: "customer_transactions");

            migrationBuilder.DropTable(
                name: "held_sale_items");

            migrationBuilder.DropTable(
                name: "refund_items");

            migrationBuilder.DropTable(
                name: "held_sales");

            migrationBuilder.DropTable(
                name: "refunds");

            migrationBuilder.DropIndex(
                name: "ix_customer_transactions_refund_id",
                table: "customer_transactions");

            migrationBuilder.DropColumn(
                name: "refund_id",
                table: "customer_transactions");
        }
    }
}
