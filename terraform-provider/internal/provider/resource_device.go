package provider

import (
	"context"
	"encoding/json"
	"fmt"

	"github.com/hashicorp/terraform-plugin-framework/diag"
	"github.com/hashicorp/terraform-plugin-framework/path"
	"github.com/hashicorp/terraform-plugin-framework/resource"
	"github.com/hashicorp/terraform-plugin-framework/resource/schema"
	"github.com/hashicorp/terraform-plugin-framework/resource/schema/booldefault"
	"github.com/hashicorp/terraform-plugin-framework/resource/schema/planmodifier"
	"github.com/hashicorp/terraform-plugin-framework/resource/schema/stringdefault"
	"github.com/hashicorp/terraform-plugin-framework/resource/schema/stringplanmodifier"
	"github.com/hashicorp/terraform-plugin-framework/types"
	"github.com/hashicorp/terraform-plugin-log/tflog"
)

// Ensure the implementation satisfies the expected interfaces.
var (
	_ resource.Resource                = &deviceResource{}
	_ resource.ResourceWithConfigure   = &deviceResource{}
	_ resource.ResourceWithImportState = &deviceResource{}
)

// NewDeviceResource is a helper function to simplify the provider implementation.
func NewDeviceResource() resource.Resource {
	return &deviceResource{}
}

// deviceResource is the resource implementation.
type deviceResource struct {
	client *IamClient
}

// deviceResourceModel maps the resource schema data.
type deviceResourceModel struct {
	ID                   types.String `tfsdk:"id"`
	DeviceID             types.String `tfsdk:"device_id"`
	Name                 types.String `tfsdk:"name"`
	DeviceType           types.String `tfsdk:"device_type"`
	AuthenticationMethod types.String `tfsdk:"authentication_method"`
	TenantID             types.String `tfsdk:"tenant_id"`
	ResourcePath         types.String `tfsdk:"resource_path"`
	Permissions          types.List   `tfsdk:"permissions"`
	Metadata             types.String `tfsdk:"metadata"`
	Tags                 types.List   `tfsdk:"tags"`
	IsActive             types.Bool   `tfsdk:"is_active"`
	IsOnline             types.Bool   `tfsdk:"is_online"`
	IsProvisioned        types.Bool   `tfsdk:"is_provisioned"`
	LastSeenAt           types.String `tfsdk:"last_seen_at"`
	SharedSecret         types.String `tfsdk:"shared_secret"`
	CreatedAt            types.String `tfsdk:"created_at"`
	UpdatedAt            types.String `tfsdk:"updated_at"`
}

// deviceAPIResponse represents the JSON response from the API.
type deviceAPIResponse struct {
	ID                   string  `json:"id"`
	DeviceID             string  `json:"deviceId"`
	Name                 string  `json:"name"`
	DeviceType           string  `json:"deviceType"`
	AuthenticationMethod string  `json:"authenticationMethod"`
	TenantID             string  `json:"tenantId"`
	TenantName           *string `json:"tenantName"`
	ResourcePath         string  `json:"resourcePath"`
	IsActive             bool    `json:"isActive"`
	IsOnline             bool    `json:"isOnline"`
	IsProvisioned        bool    `json:"isProvisioned"`
	LastSeenAt           *string `json:"lastSeenAt"`
	Metadata             *string `json:"metadata"`
	Tags                 *string `json:"tags"`
	SharedSecret         *string `json:"sharedSecret"`
	CreatedAt            string  `json:"createdAt"`
	UpdatedAt            string  `json:"updatedAt"`
}

// Metadata returns the resource type name.
func (r *deviceResource) Metadata(_ context.Context, req resource.MetadataRequest, resp *resource.MetadataResponse) {
	resp.TypeName = req.ProviderTypeName + "_device"
}

// Schema defines the schema for the resource.
func (r *deviceResource) Schema(_ context.Context, _ resource.SchemaRequest, resp *resource.SchemaResponse) {
	resp.Schema = schema.Schema{
		Description: "Manages an IoT device, service, or machine identity in the IAM system. " +
			"Supports X.509 certificate-based and HMAC token-based authentication.",
		Attributes: map[string]schema.Attribute{
			"id": schema.StringAttribute{
				Description: "The internal unique identifier of the device (UUID).",
				Computed:    true,
				PlanModifiers: []planmodifier.String{
					stringplanmodifier.UseStateForUnknown(),
				},
			},
			"device_id": schema.StringAttribute{
				Description: "Human-readable device identifier (e.g., 'acme-hq-floor3-hvac-unit247'). " +
					"Must be unique within a tenant.",
				Required: true,
				PlanModifiers: []planmodifier.String{
					stringplanmodifier.RequiresReplace(),
				},
			},
			"name": schema.StringAttribute{
				Description: "Display name for the device (e.g., 'Floor 3 HVAC Unit 247').",
				Required:    true,
			},
			"device_type": schema.StringAttribute{
				Description: "Device type category (e.g., 'hvac', 'sensor', 'lighting', 'access', 'camera', 'robot'). " +
					"Used for grouping and policy templates.",
				Required: true,
			},
			"authentication_method": schema.StringAttribute{
				Description: "Authentication method: 'certificate' (X.509 mTLS) or 'hmac' (HMAC-SHA256 shared secret).",
				Optional:    true,
				Computed:    true,
				Default:     stringdefault.StaticString("certificate"),
				PlanModifiers: []planmodifier.String{
					stringplanmodifier.RequiresReplace(),
				},
			},
			"tenant_id": schema.StringAttribute{
				Description: "The tenant (location) this device belongs to. Maps to the hierarchical resource model.",
				Required:    true,
			},
			"resource_path": schema.StringAttribute{
				Description: "Hierarchical resource path for authorization. " +
					"Format: 'org:sub-org:location:resource-type:resource-id' " +
					"(e.g., 'acme:headquarters:floor-3:hvac:unit-247').",
				Required: true,
			},
			"permissions": schema.ListAttribute{
				Description: "List of permission strings granted to this device " +
					"(e.g., ['acme:hq:floor-3:hvac:unit-247:telemetry:write']).",
				Optional:    true,
				ElementType: types.StringType,
			},
			"metadata": schema.StringAttribute{
				Description: "JSON string containing hardware/firmware metadata " +
					"(manufacturer, model, firmware version, MAC address, IP address, etc.).",
				Optional: true,
			},
			"tags": schema.ListAttribute{
				Description: "Tags for grouping and filtering (e.g., ['hvac', 'floor-3', 'critical']).",
				Optional:    true,
				ElementType: types.StringType,
			},
			"is_active": schema.BoolAttribute{
				Description: "Whether the device is active.",
				Optional:    true,
				Computed:    true,
				Default:     booldefault.StaticBool(true),
			},
			"is_online": schema.BoolAttribute{
				Description: "Whether the device is currently online. Read-only, updated by heartbeat.",
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
			"shared_secret": schema.StringAttribute{
				Description: "HMAC shared secret returned during registration for HMAC devices. " +
					"Only available once at creation time. Store securely.",
				Computed:  true,
				Sensitive: true,
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

// Configure adds the provider configured client to the resource.
func (r *deviceResource) Configure(_ context.Context, req resource.ConfigureRequest, resp *resource.ConfigureResponse) {
	if req.ProviderData == nil {
		return
	}

	client, ok := req.ProviderData.(*IamClient)
	if !ok {
		resp.Diagnostics.AddError(
			"Unexpected Resource Configure Type",
			fmt.Sprintf("Expected *IamClient, got: %T. Please report this issue to the provider developers.", req.ProviderData),
		)
		return
	}

	r.client = client
}

// Create creates the resource and sets the initial Terraform state.
func (r *deviceResource) Create(ctx context.Context, req resource.CreateRequest, resp *resource.CreateResponse) {
	var plan deviceResourceModel
	diags := req.Plan.Get(ctx, &plan)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}

	tflog.Debug(ctx, "Creating device", map[string]interface{}{
		"device_id": plan.DeviceID.ValueString(),
		"name":      plan.Name.ValueString(),
	})

	// Build request body.
	requestBody := map[string]interface{}{
		"deviceId":             plan.DeviceID.ValueString(),
		"name":                 plan.Name.ValueString(),
		"deviceType":           plan.DeviceType.ValueString(),
		"authenticationMethod": plan.AuthenticationMethod.ValueString(),
		"tenantId":             plan.TenantID.ValueString(),
		"resourcePath":         plan.ResourcePath.ValueString(),
	}

	// Parse permissions list.
	if !plan.Permissions.IsNull() && !plan.Permissions.IsUnknown() {
		var permissions []string
		diags = plan.Permissions.ElementsAs(ctx, &permissions, false)
		resp.Diagnostics.Append(diags...)
		if resp.Diagnostics.HasError() {
			return
		}
		requestBody["permissions"] = permissions
	}

	// Add metadata.
	if !plan.Metadata.IsNull() && !plan.Metadata.IsUnknown() {
		requestBody["metadata"] = plan.Metadata.ValueString()
	}

	// Parse tags list.
	if !plan.Tags.IsNull() && !plan.Tags.IsUnknown() {
		var tags []string
		diags = plan.Tags.ElementsAs(ctx, &tags, false)
		resp.Diagnostics.Append(diags...)
		if resp.Diagnostics.HasError() {
			return
		}
		requestBody["tags"] = tags
	}

	// Call the API.
	body, err := r.client.doRequest("POST", "/api/devices", requestBody)
	if err != nil {
		resp.Diagnostics.AddError(
			"Error Creating Device",
			"Could not register device, unexpected error: "+err.Error(),
		)
		return
	}

	// Parse the response.
	var device deviceAPIResponse
	if err := json.Unmarshal(body, &device); err != nil {
		resp.Diagnostics.AddError(
			"Error Parsing Response",
			"Could not parse device registration response: "+err.Error(),
		)
		return
	}

	// Map response to model.
	plan.ID = types.StringValue(device.ID)
	plan.IsActive = types.BoolValue(device.IsActive)
	plan.IsOnline = types.BoolValue(device.IsOnline)
	plan.IsProvisioned = types.BoolValue(device.IsProvisioned)
	plan.CreatedAt = types.StringValue(device.CreatedAt)
	plan.UpdatedAt = types.StringValue(device.CreatedAt)

	if device.SharedSecret != nil {
		plan.SharedSecret = types.StringValue(*device.SharedSecret)
	} else {
		plan.SharedSecret = types.StringNull()
	}

	if device.LastSeenAt != nil {
		plan.LastSeenAt = types.StringValue(*device.LastSeenAt)
	} else {
		plan.LastSeenAt = types.StringNull()
	}

	diags = resp.State.Set(ctx, plan)
	resp.Diagnostics.Append(diags...)

	tflog.Info(ctx, "Created device", map[string]interface{}{
		"id":        device.ID,
		"device_id": device.DeviceID,
	})
}

// Read refreshes the Terraform state with the latest data.
func (r *deviceResource) Read(ctx context.Context, req resource.ReadRequest, resp *resource.ReadResponse) {
	var state deviceResourceModel
	diags := req.State.Get(ctx, &state)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}

	tflog.Debug(ctx, "Reading device", map[string]interface{}{
		"id": state.ID.ValueString(),
	})

	body, statusCode, err := r.client.doRequestWithStatus("GET", fmt.Sprintf("/api/devices/%s", state.ID.ValueString()), nil)
	if err != nil {
		if statusCode == 404 {
			tflog.Warn(ctx, "Device not found, removing from state", map[string]interface{}{
				"id": state.ID.ValueString(),
			})
			resp.State.RemoveResource(ctx)
			return
		}
		resp.Diagnostics.AddError(
			"Error Reading Device",
			"Could not read device ID "+state.ID.ValueString()+": "+err.Error(),
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

	r.mapDeviceToModel(ctx, &device, &state, &resp.Diagnostics)
	if resp.Diagnostics.HasError() {
		return
	}

	diags = resp.State.Set(ctx, &state)
	resp.Diagnostics.Append(diags...)
}

// Update updates the resource and sets the updated Terraform state on success.
func (r *deviceResource) Update(ctx context.Context, req resource.UpdateRequest, resp *resource.UpdateResponse) {
	var plan deviceResourceModel
	diags := req.Plan.Get(ctx, &plan)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}

	var state deviceResourceModel
	diags = req.State.Get(ctx, &state)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}

	deviceInternalID := state.ID.ValueString()

	tflog.Debug(ctx, "Updating device", map[string]interface{}{
		"id": deviceInternalID,
	})

	// Build the update request body.
	requestBody := map[string]interface{}{}

	if plan.Name.ValueString() != state.Name.ValueString() {
		requestBody["name"] = plan.Name.ValueString()
	}

	if plan.DeviceType.ValueString() != state.DeviceType.ValueString() {
		requestBody["deviceType"] = plan.DeviceType.ValueString()
	}

	if plan.ResourcePath.ValueString() != state.ResourcePath.ValueString() {
		requestBody["resourcePath"] = plan.ResourcePath.ValueString()
	}

	if !plan.IsActive.IsNull() && !plan.IsActive.IsUnknown() {
		requestBody["isActive"] = plan.IsActive.ValueBool()
	}

	// Parse permissions list for update.
	if !plan.Permissions.IsNull() && !plan.Permissions.IsUnknown() {
		var permissions []string
		diags = plan.Permissions.ElementsAs(ctx, &permissions, false)
		resp.Diagnostics.Append(diags...)
		if resp.Diagnostics.HasError() {
			return
		}
		requestBody["permissions"] = permissions
	}

	// Add metadata.
	if !plan.Metadata.IsNull() && !plan.Metadata.IsUnknown() {
		requestBody["metadata"] = plan.Metadata.ValueString()
	}

	// Parse tags list for update.
	if !plan.Tags.IsNull() && !plan.Tags.IsUnknown() {
		var tags []string
		diags = plan.Tags.ElementsAs(ctx, &tags, false)
		resp.Diagnostics.Append(diags...)
		if resp.Diagnostics.HasError() {
			return
		}
		requestBody["tags"] = tags
	}

	// Call the API.
	body, err := r.client.doRequest("PUT", fmt.Sprintf("/api/devices/%s", deviceInternalID), requestBody)
	if err != nil {
		resp.Diagnostics.AddError(
			"Error Updating Device",
			"Could not update device ID "+deviceInternalID+": "+err.Error(),
		)
		return
	}

	// Parse the response.
	var device deviceAPIResponse
	if err := json.Unmarshal(body, &device); err != nil {
		resp.Diagnostics.AddError(
			"Error Parsing Response",
			"Could not parse device update response: "+err.Error(),
		)
		return
	}

	// Read back the full device to get all fields.
	readBody, _, readErr := r.client.doRequestWithStatus("GET", fmt.Sprintf("/api/devices/%s", deviceInternalID), nil)
	if readErr == nil {
		var fullDevice deviceAPIResponse
		if json.Unmarshal(readBody, &fullDevice) == nil {
			r.mapDeviceToModel(ctx, &fullDevice, &plan, &resp.Diagnostics)
		}
	}

	// Preserve the shared_secret from state (only available at creation).
	plan.SharedSecret = state.SharedSecret

	diags = resp.State.Set(ctx, plan)
	resp.Diagnostics.Append(diags...)

	tflog.Info(ctx, "Updated device", map[string]interface{}{
		"id": deviceInternalID,
	})
}

// Delete deletes the resource and removes the Terraform state on success.
func (r *deviceResource) Delete(ctx context.Context, req resource.DeleteRequest, resp *resource.DeleteResponse) {
	var state deviceResourceModel
	diags := req.State.Get(ctx, &state)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}

	deviceInternalID := state.ID.ValueString()

	tflog.Debug(ctx, "Deleting (deactivating) device", map[string]interface{}{
		"id": deviceInternalID,
	})

	// The IAM API deactivates devices and revokes certificates rather than hard deleting.
	_, statusCode, err := r.client.doRequestWithStatus("POST", fmt.Sprintf("/api/devices/%s/deactivate", deviceInternalID), nil)
	if err != nil {
		if statusCode == 404 {
			tflog.Warn(ctx, "Device already deleted/not found", map[string]interface{}{
				"id": deviceInternalID,
			})
			return
		}
		resp.Diagnostics.AddError(
			"Error Deactivating Device",
			"Could not deactivate device ID "+deviceInternalID+": "+err.Error(),
		)
		return
	}

	tflog.Info(ctx, "Deactivated device", map[string]interface{}{
		"id": deviceInternalID,
	})
}

// ImportState imports an existing resource by its ID.
func (r *deviceResource) ImportState(ctx context.Context, req resource.ImportStateRequest, resp *resource.ImportStateResponse) {
	resource.ImportStatePassthroughID(ctx, path.Root("id"), req, resp)
}

// mapDeviceToModel maps an API response to the Terraform model.
func (r *deviceResource) mapDeviceToModel(ctx context.Context, device *deviceAPIResponse, model *deviceResourceModel, diagnostics *diag.Diagnostics) {
	model.ID = types.StringValue(device.ID)
	model.DeviceID = types.StringValue(device.DeviceID)
	model.Name = types.StringValue(device.Name)
	model.DeviceType = types.StringValue(device.DeviceType)
	model.AuthenticationMethod = types.StringValue(device.AuthenticationMethod)
	model.TenantID = types.StringValue(device.TenantID)
	model.ResourcePath = types.StringValue(device.ResourcePath)
	model.IsActive = types.BoolValue(device.IsActive)
	model.IsOnline = types.BoolValue(device.IsOnline)
	model.IsProvisioned = types.BoolValue(device.IsProvisioned)
	model.CreatedAt = types.StringValue(device.CreatedAt)
	model.UpdatedAt = types.StringValue(device.UpdatedAt)

	if device.LastSeenAt != nil {
		model.LastSeenAt = types.StringValue(*device.LastSeenAt)
	} else {
		model.LastSeenAt = types.StringNull()
	}

	if device.Metadata != nil {
		model.Metadata = types.StringValue(*device.Metadata)
	} else {
		model.Metadata = types.StringNull()
	}

	// Parse tags JSON array from the API response into a list.
	if device.Tags != nil && *device.Tags != "" && *device.Tags != "[]" {
		var tags []string
		if err := json.Unmarshal([]byte(*device.Tags), &tags); err == nil {
			tagList, diags := types.ListValueFrom(ctx, types.StringType, tags)
			diagnostics.Append(diags...)
			model.Tags = tagList
		}
	} else {
		model.Tags = types.ListNull(types.StringType)
	}
}
