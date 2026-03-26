package provider

import (
	"context"
	"encoding/json"
	"fmt"

	"github.com/hashicorp/terraform-plugin-framework/datasource"
	"github.com/hashicorp/terraform-plugin-framework/datasource/schema"
	"github.com/hashicorp/terraform-plugin-framework/types"
	"github.com/hashicorp/terraform-plugin-log/tflog"
)

// Ensure the implementation satisfies the expected interfaces.
var (
	_ datasource.DataSource              = &tenantDataSource{}
	_ datasource.DataSourceWithConfigure = &tenantDataSource{}
)

// NewTenantDataSource is a helper function to simplify the provider implementation.
func NewTenantDataSource() datasource.DataSource {
	return &tenantDataSource{}
}

// tenantDataSource is the data source implementation.
type tenantDataSource struct {
	client *IamClient
}

// tenantDataSourceModel maps the data source schema data.
type tenantDataSourceModel struct {
	ID             types.String `tfsdk:"id"`
	Name           types.String `tfsdk:"name"`
	Slug           types.String `tfsdk:"slug"`
	Type           types.String `tfsdk:"type"`
	ParentTenantID types.String `tfsdk:"parent_tenant_id"`
	Metadata       types.String `tfsdk:"metadata"`
	Settings       types.String `tfsdk:"settings"`
	IsActive       types.Bool   `tfsdk:"is_active"`
	ChildCount     types.Int64  `tfsdk:"child_count"`
	CreatedAt      types.String `tfsdk:"created_at"`
	UpdatedAt      types.String `tfsdk:"updated_at"`
}

// Metadata returns the data source type name.
func (d *tenantDataSource) Metadata(_ context.Context, req datasource.MetadataRequest, resp *datasource.MetadataResponse) {
	resp.TypeName = req.ProviderTypeName + "_tenant"
}

// Schema defines the schema for the data source.
func (d *tenantDataSource) Schema(_ context.Context, _ datasource.SchemaRequest, resp *datasource.SchemaResponse) {
	resp.Schema = schema.Schema{
		Description: "Use this data source to get information about an existing tenant in the IAM system.",
		Attributes: map[string]schema.Attribute{
			"id": schema.StringAttribute{
				Description: "The unique identifier of the tenant (UUID).",
				Required:    true,
			},
			"name": schema.StringAttribute{
				Description: "The display name of the tenant.",
				Computed:    true,
			},
			"slug": schema.StringAttribute{
				Description: "URL-friendly slug for the tenant.",
				Computed:    true,
			},
			"type": schema.StringAttribute{
				Description: "The type of tenant (Organization, Building, Floor, Room, Device).",
				Computed:    true,
			},
			"parent_tenant_id": schema.StringAttribute{
				Description: "The ID of the parent tenant.",
				Computed:    true,
			},
			"metadata": schema.StringAttribute{
				Description: "JSON string containing descriptive metadata.",
				Computed:    true,
			},
			"settings": schema.StringAttribute{
				Description: "JSON string containing tenant-specific configuration.",
				Computed:    true,
			},
			"is_active": schema.BoolAttribute{
				Description: "Whether the tenant is active.",
				Computed:    true,
			},
			"child_count": schema.Int64Attribute{
				Description: "Number of direct child tenants.",
				Computed:    true,
			},
			"created_at": schema.StringAttribute{
				Description: "Timestamp when the tenant was created.",
				Computed:    true,
			},
			"updated_at": schema.StringAttribute{
				Description: "Timestamp when the tenant was last updated.",
				Computed:    true,
			},
		},
	}
}

// Configure adds the provider configured client to the data source.
func (d *tenantDataSource) Configure(_ context.Context, req datasource.ConfigureRequest, resp *datasource.ConfigureResponse) {
	if req.ProviderData == nil {
		return
	}

	client, ok := req.ProviderData.(*IamClient)
	if !ok {
		resp.Diagnostics.AddError(
			"Unexpected Data Source Configure Type",
			fmt.Sprintf("Expected *IamClient, got: %T. Please report this issue to the provider developers.", req.ProviderData),
		)
		return
	}

	d.client = client
}

// Read refreshes the Terraform state with the latest data.
func (d *tenantDataSource) Read(ctx context.Context, req datasource.ReadRequest, resp *datasource.ReadResponse) {
	var config tenantDataSourceModel
	diags := req.Config.Get(ctx, &config)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}

	tenantID := config.ID.ValueString()

	tflog.Debug(ctx, "Reading tenant data source", map[string]interface{}{
		"id": tenantID,
	})

	body, _, err := d.client.doRequestWithStatus("GET", fmt.Sprintf("/api/tenants/%s", tenantID), nil)
	if err != nil {
		resp.Diagnostics.AddError(
			"Error Reading Tenant",
			"Could not read tenant ID "+tenantID+": "+err.Error(),
		)
		return
	}

	var tenant tenantAPIResponse
	if err := json.Unmarshal(body, &tenant); err != nil {
		resp.Diagnostics.AddError(
			"Error Parsing Response",
			"Could not parse tenant read response: "+err.Error(),
		)
		return
	}

	// Map response to model.
	config.ID = types.StringValue(tenant.ID)
	config.Name = types.StringValue(tenant.Name)
	config.Slug = types.StringValue(tenant.Slug)
	config.Type = types.StringValue(tenant.Type)

	if tenant.ParentTenantID != nil {
		config.ParentTenantID = types.StringValue(*tenant.ParentTenantID)
	} else {
		config.ParentTenantID = types.StringNull()
	}

	metadataJSON, _ := json.Marshal(tenant.Metadata)
	if string(metadataJSON) == "null" {
		metadataJSON = []byte("{}")
	}
	config.Metadata = types.StringValue(string(metadataJSON))

	settingsJSON, _ := json.Marshal(tenant.Settings)
	if string(settingsJSON) == "null" {
		settingsJSON = []byte("{}")
	}
	config.Settings = types.StringValue(string(settingsJSON))

	config.IsActive = types.BoolValue(tenant.IsActive)
	config.ChildCount = types.Int64Value(int64(tenant.ChildCount))
	config.CreatedAt = types.StringValue(tenant.CreatedAt)
	config.UpdatedAt = types.StringValue(tenant.UpdatedAt)

	diags = resp.State.Set(ctx, &config)
	resp.Diagnostics.Append(diags...)
}
