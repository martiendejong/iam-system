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
	_ datasource.DataSource              = &deviceDataSource{}
	_ datasource.DataSourceWithConfigure = &deviceDataSource{}
)

// NewDeviceDataSource is a helper function to simplify the provider implementation.
func NewDeviceDataSource() datasource.DataSource {
	return &deviceDataSource{}
}

// deviceDataSource is the data source implementation.
type deviceDataSource struct {
	client *IamClient
}

// deviceDataSourceModel maps the data source schema data.
type deviceDataSourceModel struct {
	ID                   types.String `tfsdk:"id"`
	DeviceID             types.String `tfsdk:"device_id"`
	Name                 types.String `tfsdk:"name"`
	DeviceType           types.String `tfsdk:"device_type"`
	AuthenticationMethod types.String `tfsdk:"authentication_method"`
	TenantID             types.String `tfsdk:"tenant_id"`
	TenantName           types.String `tfsdk:"tenant_name"`
	ResourcePath         types.String `tfsdk:"resource_path"`
	IsActive             types.Bool   `tfsdk:"is_active"`
	IsOnline             types.Bool   `tfsdk:"is_online"`
	IsProvisioned        types.Bool   `tfsdk:"is_provisioned"`
	LastSeenAt           types.String `tfsdk:"last_seen_at"`
	Metadata             types.String `tfsdk:"metadata"`
	Tags                 types.String `tfsdk:"tags"`
	CreatedAt            types.String `tfsdk:"created_at"`
	UpdatedAt            types.String `tfsdk:"updated_at"`
}

// Metadata returns the data source type name.
func (d *deviceDataSource) Metadata(_ context.Context, req datasource.MetadataRequest, resp *datasource.MetadataResponse) {
	resp.TypeName = req.ProviderTypeName + "_device"
}

// Schema defines the schema for the data source.
func (d *deviceDataSource) Schema(_ context.Context, _ datasource.SchemaRequest, resp *datasource.SchemaResponse) {
	resp.Schema = schema.Schema{
		Description: "Use this data source to get information about an existing device in the IAM system. " +
			"You can look up a device by its internal UUID or by its human-readable device ID.",
		Attributes: map[string]schema.Attribute{
			"id": schema.StringAttribute{
				Description: "The internal unique identifier of the device (UUID). " +
					"Either 'id' or 'device_id' must be specified.",
				Optional: true,
				Computed: true,
			},
			"device_id": schema.StringAttribute{
				Description: "Human-readable device identifier. " +
					"Either 'id' or 'device_id' must be specified.",
				Optional: true,
				Computed: true,
			},
			"name": schema.StringAttribute{
				Description: "Display name for the device.",
				Computed:    true,
			},
			"device_type": schema.StringAttribute{
				Description: "Device type category.",
				Computed:    true,
			},
			"authentication_method": schema.StringAttribute{
				Description: "Authentication method: 'certificate' or 'hmac'.",
				Computed:    true,
			},
			"tenant_id": schema.StringAttribute{
				Description: "The tenant this device belongs to.",
				Computed:    true,
			},
			"tenant_name": schema.StringAttribute{
				Description: "The name of the tenant this device belongs to.",
				Computed:    true,
			},
			"resource_path": schema.StringAttribute{
				Description: "Hierarchical resource path for authorization.",
				Computed:    true,
			},
			"is_active": schema.BoolAttribute{
				Description: "Whether the device is active.",
				Computed:    true,
			},
			"is_online": schema.BoolAttribute{
				Description: "Whether the device is currently online.",
				Computed:    true,
			},
			"is_provisioned": schema.BoolAttribute{
				Description: "Whether the device has been provisioned.",
				Computed:    true,
			},
			"last_seen_at": schema.StringAttribute{
				Description: "Timestamp of the last heartbeat from the device.",
				Computed:    true,
			},
			"metadata": schema.StringAttribute{
				Description: "JSON string containing hardware/firmware metadata.",
				Computed:    true,
			},
			"tags": schema.StringAttribute{
				Description: "JSON string containing tags for grouping and filtering.",
				Computed:    true,
			},
			"created_at": schema.StringAttribute{
				Description: "Timestamp when the device was created.",
				Computed:    true,
			},
			"updated_at": schema.StringAttribute{
				Description: "Timestamp when the device was last updated.",
				Computed:    true,
			},
		},
	}
}

// Configure adds the provider configured client to the data source.
func (d *deviceDataSource) Configure(_ context.Context, req datasource.ConfigureRequest, resp *datasource.ConfigureResponse) {
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
func (d *deviceDataSource) Read(ctx context.Context, req datasource.ReadRequest, resp *datasource.ReadResponse) {
	var config deviceDataSourceModel
	diags := req.Config.Get(ctx, &config)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}

	// Determine which lookup method to use.
	var apiPath string
	if !config.ID.IsNull() && !config.ID.IsUnknown() {
		apiPath = fmt.Sprintf("/api/devices/%s", config.ID.ValueString())
		tflog.Debug(ctx, "Reading device data source by ID", map[string]interface{}{
			"id": config.ID.ValueString(),
		})
	} else if !config.DeviceID.IsNull() && !config.DeviceID.IsUnknown() {
		apiPath = fmt.Sprintf("/api/devices/by-device-id/%s", config.DeviceID.ValueString())
		tflog.Debug(ctx, "Reading device data source by device_id", map[string]interface{}{
			"device_id": config.DeviceID.ValueString(),
		})
	} else {
		resp.Diagnostics.AddError(
			"Missing Device Identifier",
			"Either 'id' (internal UUID) or 'device_id' (human-readable ID) must be specified.",
		)
		return
	}

	body, _, err := d.client.doRequestWithStatus("GET", apiPath, nil)
	if err != nil {
		resp.Diagnostics.AddError(
			"Error Reading Device",
			"Could not read device: "+err.Error(),
		)
		return
	}

	var device deviceAPIResponse
	if err := json.Unmarshal(body, &device); err != nil {
		resp.Diagnostics.AddError(
			"Error Parsing Response",
			"Could not parse device read response: "+err.Error(),
		)
		return
	}

	// Map response to model.
	config.ID = types.StringValue(device.ID)
	config.DeviceID = types.StringValue(device.DeviceID)
	config.Name = types.StringValue(device.Name)
	config.DeviceType = types.StringValue(device.DeviceType)
	config.AuthenticationMethod = types.StringValue(device.AuthenticationMethod)
	config.TenantID = types.StringValue(device.TenantID)
	config.ResourcePath = types.StringValue(device.ResourcePath)
	config.IsActive = types.BoolValue(device.IsActive)
	config.IsOnline = types.BoolValue(device.IsOnline)
	config.IsProvisioned = types.BoolValue(device.IsProvisioned)
	config.CreatedAt = types.StringValue(device.CreatedAt)
	config.UpdatedAt = types.StringValue(device.UpdatedAt)

	if device.TenantName != nil {
		config.TenantName = types.StringValue(*device.TenantName)
	} else {
		config.TenantName = types.StringNull()
	}

	if device.LastSeenAt != nil {
		config.LastSeenAt = types.StringValue(*device.LastSeenAt)
	} else {
		config.LastSeenAt = types.StringNull()
	}

	if device.Metadata != nil {
		config.Metadata = types.StringValue(*device.Metadata)
	} else {
		config.Metadata = types.StringNull()
	}

	if device.Tags != nil {
		config.Tags = types.StringValue(*device.Tags)
	} else {
		config.Tags = types.StringNull()
	}

	diags = resp.State.Set(ctx, &config)
	resp.Diagnostics.Append(diags...)
}
