package provider

import (
	"context"
	"encoding/json"
	"fmt"

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
	_ resource.Resource                = &tenantResource{}
	_ resource.ResourceWithConfigure   = &tenantResource{}
	_ resource.ResourceWithImportState = &tenantResource{}
)

// NewTenantResource is a helper function to simplify the provider implementation.
func NewTenantResource() resource.Resource {
	return &tenantResource{}
}

// tenantResource is the resource implementation.
type tenantResource struct {
	client *IamClient
}

// tenantResourceModel maps the resource schema data.
type tenantResourceModel struct {
	ID             types.String `tfsdk:"id"`
	Name           types.String `tfsdk:"name"`
	Slug           types.String `tfsdk:"slug"`
	Type           types.String `tfsdk:"type"`
	ParentTenantID types.String `tfsdk:"parent_tenant_id"`
	Metadata       types.String `tfsdk:"metadata"`
	Settings       types.String `tfsdk:"settings"`
	IsActive       types.Bool   `tfsdk:"is_active"`
	CreatedAt      types.String `tfsdk:"created_at"`
	UpdatedAt      types.String `tfsdk:"updated_at"`
}

// tenantAPIResponse represents the JSON response from the API.
type tenantAPIResponse struct {
	ID               string                 `json:"id"`
	Name             string                 `json:"name"`
	Slug             string                 `json:"slug"`
	Type             string                 `json:"type"`
	ParentTenantID   *string                `json:"parentTenantId"`
	ParentTenantName *string                `json:"parentTenantName"`
	ChildCount       int                    `json:"childCount"`
	Metadata         map[string]interface{} `json:"metadata"`
	Settings         map[string]interface{} `json:"settings"`
	IsActive         bool                   `json:"isActive"`
	CreatedAt        string                 `json:"createdAt"`
	UpdatedAt        string                 `json:"updatedAt"`
}

// Metadata returns the resource type name.
func (r *tenantResource) Metadata(_ context.Context, req resource.MetadataRequest, resp *resource.MetadataResponse) {
	resp.TypeName = req.ProviderTypeName + "_tenant"
}

// Schema defines the schema for the resource.
func (r *tenantResource) Schema(_ context.Context, _ resource.SchemaRequest, resp *resource.SchemaResponse) {
	resp.Schema = schema.Schema{
		Description: "Manages a tenant in the IAM system. Tenants are hierarchical and can represent " +
			"organizations, buildings, floors, rooms, or device groups.",
		Attributes: map[string]schema.Attribute{
			"id": schema.StringAttribute{
				Description: "The unique identifier of the tenant (UUID).",
				Computed:    true,
				PlanModifiers: []planmodifier.String{
					stringplanmodifier.UseStateForUnknown(),
				},
			},
			"name": schema.StringAttribute{
				Description: "The display name of the tenant.",
				Required:    true,
			},
			"slug": schema.StringAttribute{
				Description: "URL-friendly slug for the tenant. Auto-generated from name if not provided.",
				Computed:    true,
			},
			"type": schema.StringAttribute{
				Description: "The type of tenant. Valid values: Organization, Building, Floor, Room, Device.",
				Optional:    true,
				Computed:    true,
				Default:     stringdefault.StaticString("Organization"),
			},
			"parent_tenant_id": schema.StringAttribute{
				Description: "The ID of the parent tenant for hierarchical relationships. " +
					"Null for root tenants.",
				Optional: true,
			},
			"metadata": schema.StringAttribute{
				Description: "JSON string containing descriptive metadata (address, floor number, " +
					"room capacity, device model, etc.).",
				Optional: true,
				Computed: true,
				Default:  stringdefault.StaticString("{}"),
			},
			"settings": schema.StringAttribute{
				Description: "JSON string containing tenant-specific configuration (temperature " +
					"thresholds, access hours, etc.).",
				Optional: true,
				Computed: true,
				Default:  stringdefault.StaticString("{}"),
			},
			"is_active": schema.BoolAttribute{
				Description: "Whether the tenant is active.",
				Optional:    true,
				Computed:    true,
				Default:     booldefault.StaticBool(true),
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

// Configure adds the provider configured client to the resource.
func (r *tenantResource) Configure(_ context.Context, req resource.ConfigureRequest, resp *resource.ConfigureResponse) {
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
func (r *tenantResource) Create(ctx context.Context, req resource.CreateRequest, resp *resource.CreateResponse) {
	// Retrieve values from plan.
	var plan tenantResourceModel
	diags := req.Plan.Get(ctx, &plan)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}

	tflog.Debug(ctx, "Creating tenant", map[string]interface{}{
		"name": plan.Name.ValueString(),
		"type": plan.Type.ValueString(),
	})

	// Build the request body.
	requestBody := map[string]interface{}{
		"name": plan.Name.ValueString(),
		"type": plan.Type.ValueString(),
	}

	if !plan.ParentTenantID.IsNull() && !plan.ParentTenantID.IsUnknown() {
		requestBody["parentTenantId"] = plan.ParentTenantID.ValueString()
	}

	// Parse metadata JSON string into map.
	if !plan.Metadata.IsNull() && !plan.Metadata.IsUnknown() && plan.Metadata.ValueString() != "{}" {
		var metadata map[string]interface{}
		if err := json.Unmarshal([]byte(plan.Metadata.ValueString()), &metadata); err != nil {
			resp.Diagnostics.AddError("Invalid Metadata", "Could not parse metadata JSON: "+err.Error())
			return
		}
		requestBody["metadata"] = metadata
	}

	// Parse settings JSON string into map.
	if !plan.Settings.IsNull() && !plan.Settings.IsUnknown() && plan.Settings.ValueString() != "{}" {
		var settings map[string]interface{}
		if err := json.Unmarshal([]byte(plan.Settings.ValueString()), &settings); err != nil {
			resp.Diagnostics.AddError("Invalid Settings", "Could not parse settings JSON: "+err.Error())
			return
		}
		requestBody["settings"] = settings
	}

	// Call the API.
	body, err := r.client.doRequest("POST", "/api/tenants", requestBody)
	if err != nil {
		resp.Diagnostics.AddError(
			"Error Creating Tenant",
			"Could not create tenant, unexpected error: "+err.Error(),
		)
		return
	}

	// Parse the response.
	var tenant tenantAPIResponse
	if err := json.Unmarshal(body, &tenant); err != nil {
		resp.Diagnostics.AddError(
			"Error Parsing Response",
			"Could not parse tenant creation response: "+err.Error(),
		)
		return
	}

	// Map response to model.
	plan.ID = types.StringValue(tenant.ID)
	plan.Slug = types.StringValue(tenant.Slug)
	plan.CreatedAt = types.StringValue(tenant.CreatedAt)
	plan.UpdatedAt = types.StringValue(tenant.CreatedAt) // Same as created_at on creation.

	if tenant.ParentTenantID != nil {
		plan.ParentTenantID = types.StringValue(*tenant.ParentTenantID)
	}

	metadataJSON, _ := json.Marshal(tenant.Metadata)
	plan.Metadata = types.StringValue(string(metadataJSON))

	settingsJSON, _ := json.Marshal(tenant.Settings)
	plan.Settings = types.StringValue(string(settingsJSON))

	plan.IsActive = types.BoolValue(tenant.IsActive)

	// Set state.
	diags = resp.State.Set(ctx, plan)
	resp.Diagnostics.Append(diags...)

	tflog.Info(ctx, "Created tenant", map[string]interface{}{
		"id":   tenant.ID,
		"name": tenant.Name,
	})
}

// Read refreshes the Terraform state with the latest data.
func (r *tenantResource) Read(ctx context.Context, req resource.ReadRequest, resp *resource.ReadResponse) {
	// Get current state.
	var state tenantResourceModel
	diags := req.State.Get(ctx, &state)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}

	tflog.Debug(ctx, "Reading tenant", map[string]interface{}{
		"id": state.ID.ValueString(),
	})

	// Call the API.
	body, statusCode, err := r.client.doRequestWithStatus("GET", fmt.Sprintf("/api/tenants/%s", state.ID.ValueString()), nil)
	if err != nil {
		// If 404, the resource was deleted outside of Terraform.
		if statusCode == 404 {
			tflog.Warn(ctx, "Tenant not found, removing from state", map[string]interface{}{
				"id": state.ID.ValueString(),
			})
			resp.State.RemoveResource(ctx)
			return
		}
		resp.Diagnostics.AddError(
			"Error Reading Tenant",
			"Could not read tenant ID "+state.ID.ValueString()+": "+err.Error(),
		)
		return
	}

	// Parse the response.
	var tenant tenantAPIResponse
	if err := json.Unmarshal(body, &tenant); err != nil {
		resp.Diagnostics.AddError(
			"Error Parsing Response",
			"Could not parse tenant read response: "+err.Error(),
		)
		return
	}

	// Map response to model.
	state.ID = types.StringValue(tenant.ID)
	state.Name = types.StringValue(tenant.Name)
	state.Slug = types.StringValue(tenant.Slug)
	state.Type = types.StringValue(tenant.Type)

	if tenant.ParentTenantID != nil {
		state.ParentTenantID = types.StringValue(*tenant.ParentTenantID)
	} else {
		state.ParentTenantID = types.StringNull()
	}

	metadataJSON, _ := json.Marshal(tenant.Metadata)
	if string(metadataJSON) == "null" {
		metadataJSON = []byte("{}")
	}
	state.Metadata = types.StringValue(string(metadataJSON))

	settingsJSON, _ := json.Marshal(tenant.Settings)
	if string(settingsJSON) == "null" {
		settingsJSON = []byte("{}")
	}
	state.Settings = types.StringValue(string(settingsJSON))

	state.IsActive = types.BoolValue(tenant.IsActive)
	state.CreatedAt = types.StringValue(tenant.CreatedAt)
	state.UpdatedAt = types.StringValue(tenant.UpdatedAt)

	// Set state.
	diags = resp.State.Set(ctx, &state)
	resp.Diagnostics.Append(diags...)
}

// Update updates the resource and sets the updated Terraform state on success.
func (r *tenantResource) Update(ctx context.Context, req resource.UpdateRequest, resp *resource.UpdateResponse) {
	// Retrieve values from plan.
	var plan tenantResourceModel
	diags := req.Plan.Get(ctx, &plan)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}

	// Retrieve current state for the ID.
	var state tenantResourceModel
	diags = req.State.Get(ctx, &state)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}

	tflog.Debug(ctx, "Updating tenant", map[string]interface{}{
		"id": state.ID.ValueString(),
	})

	// Build the request body.
	requestBody := map[string]interface{}{
		"name": plan.Name.ValueString(),
		"type": plan.Type.ValueString(),
	}

	if !plan.Metadata.IsNull() && !plan.Metadata.IsUnknown() {
		var metadata map[string]interface{}
		if err := json.Unmarshal([]byte(plan.Metadata.ValueString()), &metadata); err == nil {
			requestBody["metadata"] = metadata
		}
	}

	if !plan.Settings.IsNull() && !plan.Settings.IsUnknown() {
		var settings map[string]interface{}
		if err := json.Unmarshal([]byte(plan.Settings.ValueString()), &settings); err == nil {
			requestBody["settings"] = settings
		}
	}

	if !plan.IsActive.IsNull() && !plan.IsActive.IsUnknown() {
		requestBody["isActive"] = plan.IsActive.ValueBool()
	}

	// Call the API.
	body, err := r.client.doRequest("PUT", fmt.Sprintf("/api/tenants/%s", state.ID.ValueString()), requestBody)
	if err != nil {
		resp.Diagnostics.AddError(
			"Error Updating Tenant",
			"Could not update tenant ID "+state.ID.ValueString()+": "+err.Error(),
		)
		return
	}

	// Parse the response.
	var tenant tenantAPIResponse
	if err := json.Unmarshal(body, &tenant); err != nil {
		resp.Diagnostics.AddError(
			"Error Parsing Response",
			"Could not parse tenant update response: "+err.Error(),
		)
		return
	}

	// Map response to model, preserving immutable fields from state.
	plan.ID = state.ID
	plan.Slug = types.StringValue(tenant.Slug)
	plan.CreatedAt = state.CreatedAt
	plan.UpdatedAt = types.StringValue(tenant.UpdatedAt)
	plan.IsActive = types.BoolValue(tenant.IsActive)

	metadataJSON, _ := json.Marshal(tenant.Metadata)
	if string(metadataJSON) == "null" {
		metadataJSON = []byte("{}")
	}
	plan.Metadata = types.StringValue(string(metadataJSON))

	settingsJSON, _ := json.Marshal(tenant.Settings)
	if string(settingsJSON) == "null" {
		settingsJSON = []byte("{}")
	}
	plan.Settings = types.StringValue(string(settingsJSON))

	// Set state.
	diags = resp.State.Set(ctx, plan)
	resp.Diagnostics.Append(diags...)

	tflog.Info(ctx, "Updated tenant", map[string]interface{}{
		"id": state.ID.ValueString(),
	})
}

// Delete deletes the resource and removes the Terraform state on success.
func (r *tenantResource) Delete(ctx context.Context, req resource.DeleteRequest, resp *resource.DeleteResponse) {
	// Retrieve values from state.
	var state tenantResourceModel
	diags := req.State.Get(ctx, &state)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}

	tflog.Debug(ctx, "Deleting tenant", map[string]interface{}{
		"id": state.ID.ValueString(),
	})

	// Call the API.
	_, statusCode, err := r.client.doRequestWithStatus("DELETE", fmt.Sprintf("/api/tenants/%s", state.ID.ValueString()), nil)
	if err != nil {
		// If 404, the resource was already deleted.
		if statusCode == 404 {
			tflog.Warn(ctx, "Tenant already deleted", map[string]interface{}{
				"id": state.ID.ValueString(),
			})
			return
		}
		resp.Diagnostics.AddError(
			"Error Deleting Tenant",
			"Could not delete tenant ID "+state.ID.ValueString()+": "+err.Error(),
		)
		return
	}

	tflog.Info(ctx, "Deleted tenant", map[string]interface{}{
		"id": state.ID.ValueString(),
	})
}

// ImportState imports an existing resource by its ID.
func (r *tenantResource) ImportState(ctx context.Context, req resource.ImportStateRequest, resp *resource.ImportStateResponse) {
	resource.ImportStatePassthroughID(ctx, path.Root("id"), req, resp)
}
