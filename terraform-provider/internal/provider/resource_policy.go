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
	"github.com/hashicorp/terraform-plugin-framework/resource/schema/int64default"
	"github.com/hashicorp/terraform-plugin-framework/resource/schema/planmodifier"
	"github.com/hashicorp/terraform-plugin-framework/resource/schema/stringdefault"
	"github.com/hashicorp/terraform-plugin-framework/resource/schema/stringplanmodifier"
	"github.com/hashicorp/terraform-plugin-framework/types"
	"github.com/hashicorp/terraform-plugin-log/tflog"
)

// Ensure the implementation satisfies the expected interfaces.
var (
	_ resource.Resource                = &policyResource{}
	_ resource.ResourceWithConfigure   = &policyResource{}
	_ resource.ResourceWithImportState = &policyResource{}
)

// NewPolicyResource is a helper function to simplify the provider implementation.
func NewPolicyResource() resource.Resource {
	return &policyResource{}
}

// policyResource is the resource implementation.
type policyResource struct {
	client *IamClient
}

// policyResourceModel maps the resource schema data.
type policyResourceModel struct {
	ID               types.String `tfsdk:"id"`
	Name             types.String `tfsdk:"name"`
	Description      types.String `tfsdk:"description"`
	TenantID         types.String `tfsdk:"tenant_id"`
	InheritanceScope types.String `tfsdk:"inheritance_scope"`
	RoleID           types.String `tfsdk:"role_id"`
	UserID           types.String `tfsdk:"user_id"`
	Resource         types.String `tfsdk:"resource_path"`
	Action           types.String `tfsdk:"action"`
	Effect           types.String `tfsdk:"effect"`
	Priority         types.Int64  `tfsdk:"priority"`
	TimeConstraints  types.String `tfsdk:"time_constraints"`
	Conditions       types.String `tfsdk:"conditions"`
	ExpiresAt        types.String `tfsdk:"expires_at"`
	IsActive         types.Bool   `tfsdk:"is_active"`
	CreatedAt        types.String `tfsdk:"created_at"`
	UpdatedAt        types.String `tfsdk:"updated_at"`
	CreatedByUserID  types.String `tfsdk:"created_by_user_id"`
}

// policyAPIResponse represents the JSON response from the API.
type policyAPIResponse struct {
	ID                    string      `json:"id"`
	Name                  string      `json:"name"`
	Description           string      `json:"description"`
	TenantID              string      `json:"tenantId"`
	TenantName            *string     `json:"tenantName"`
	InheritanceScope      string      `json:"inheritanceScope"`
	InheritedFromPolicyID *string     `json:"inheritedFromPolicyId"`
	RoleID                *string     `json:"roleId"`
	RoleName              *string     `json:"roleName"`
	UserID                *string     `json:"userId"`
	UserName              *string     `json:"userName"`
	Resource              string      `json:"resource"`
	Action                string      `json:"action"`
	Effect                string      `json:"effect"`
	Priority              int         `json:"priority"`
	TimeConstraints       interface{} `json:"timeConstraints"`
	Conditions            interface{} `json:"conditions"`
	ExpiresAt             *string     `json:"expiresAt"`
	IsActive              bool        `json:"isActive"`
	CreatedAt             string      `json:"createdAt"`
	UpdatedAt             string      `json:"updatedAt"`
	CreatedByUserID       string      `json:"createdByUserId"`
	UpdatedByUserID       *string     `json:"updatedByUserId"`
}

// Metadata returns the resource type name.
func (r *policyResource) Metadata(_ context.Context, req resource.MetadataRequest, resp *resource.MetadataResponse) {
	resp.TypeName = req.ProviderTypeName + "_policy"
}

// Schema defines the schema for the resource.
func (r *policyResource) Schema(_ context.Context, _ resource.SchemaRequest, resp *resource.SchemaResponse) {
	resp.Schema = schema.Schema{
		Description: "Manages an access control policy in the IAM system. Policies define who can access " +
			"what resources, with support for spatial inheritance across the tenant hierarchy, " +
			"time-based constraints, and conditional evaluation.",
		Attributes: map[string]schema.Attribute{
			"id": schema.StringAttribute{
				Description: "The unique identifier of the policy (UUID).",
				Computed:    true,
				PlanModifiers: []planmodifier.String{
					stringplanmodifier.UseStateForUnknown(),
				},
			},
			"name": schema.StringAttribute{
				Description: "Human-readable policy name.",
				Required:    true,
			},
			"description": schema.StringAttribute{
				Description: "Detailed description of what this policy controls.",
				Optional:    true,
				Computed:    true,
				Default:     stringdefault.StaticString(""),
			},
			"tenant_id": schema.StringAttribute{
				Description: "Tenant (scope) where this policy is defined. Can be Organization, Building, " +
					"Floor, Room, or Device.",
				Required: true,
			},
			"inheritance_scope": schema.StringAttribute{
				Description: "Defines how this policy applies to child tenants in the hierarchy. " +
					"Valid values: 'Self' (no inheritance), 'Children' (direct children only), " +
					"'Descendants' (all descendants recursively).",
				Optional: true,
				Computed: true,
				Default:  stringdefault.StaticString("Self"),
			},
			"role_id": schema.StringAttribute{
				Description: "Role required to have this access. Null means the policy applies to " +
					"all authenticated users.",
				Optional: true,
			},
			"user_id": schema.StringAttribute{
				Description: "Specific user this policy applies to. Null means the policy applies " +
					"based on role or to all authenticated users.",
				Optional: true,
			},
			"resource_path": schema.StringAttribute{
				Description: "Resource this policy grants access to. " +
					"Format: 'Resource:Action' (e.g., 'Door:Unlock', 'Camera:View', 'HVAC:Control'). " +
					"Supports wildcards with '*'.",
				Required: true,
			},
			"action": schema.StringAttribute{
				Description: "Action allowed on the resource (View, Create, Update, Delete, Execute, " +
					"Control, etc.).",
				Required: true,
			},
			"effect": schema.StringAttribute{
				Description: "Effect of this policy: 'Allow' or 'Deny'. Deny policies override Allow " +
					"policies (explicit deny wins).",
				Optional: true,
				Computed: true,
				Default:  stringdefault.StaticString("Allow"),
			},
			"priority": schema.Int64Attribute{
				Description: "Priority for conflict resolution. Higher priority wins. " +
					"Default: 0. Inherited policies have priority of parent - 1.",
				Optional: true,
				Computed: true,
				Default:  int64default.StaticInt64(0),
			},
			"time_constraints": schema.StringAttribute{
				Description: "JSON string containing time-based constraints. " +
					"Format: {\"start_time\": \"09:00\", \"end_time\": \"17:00\", " +
					"\"days_of_week\": [1,2,3,4,5], \"timezone\": \"UTC\"}.",
				Optional: true,
			},
			"conditions": schema.StringAttribute{
				Description: "JSON string containing conditional constraints. " +
					"Format: {\"ip_whitelist\": [\"10.0.0.0/8\"], \"device_health\": \"trusted\", " +
					"\"location\": \"geofence:headquarters\"}.",
				Optional: true,
			},
			"expires_at": schema.StringAttribute{
				Description: "Policy expiration date in ISO 8601 format. Null means no expiration.",
				Optional:    true,
			},
			"is_active": schema.BoolAttribute{
				Description: "Whether the policy is active.",
				Optional:    true,
				Computed:    true,
				Default:     booldefault.StaticBool(true),
			},
			"created_at": schema.StringAttribute{
				Description: "Timestamp when the policy was created.",
				Computed:    true,
			},
			"updated_at": schema.StringAttribute{
				Description: "Timestamp when the policy was last updated.",
				Computed:    true,
			},
			"created_by_user_id": schema.StringAttribute{
				Description: "The ID of the user who created this policy.",
				Computed:    true,
			},
		},
	}
}

// Configure adds the provider configured client to the resource.
func (r *policyResource) Configure(_ context.Context, req resource.ConfigureRequest, resp *resource.ConfigureResponse) {
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
func (r *policyResource) Create(ctx context.Context, req resource.CreateRequest, resp *resource.CreateResponse) {
	var plan policyResourceModel
	diags := req.Plan.Get(ctx, &plan)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}

	tflog.Debug(ctx, "Creating policy", map[string]interface{}{
		"name":      plan.Name.ValueString(),
		"tenant_id": plan.TenantID.ValueString(),
	})

	// Build request body.
	requestBody := map[string]interface{}{
		"name":             plan.Name.ValueString(),
		"tenantId":         plan.TenantID.ValueString(),
		"resource":         plan.Resource.ValueString(),
		"action":           plan.Action.ValueString(),
		"effect":           plan.Effect.ValueString(),
		"inheritanceScope": plan.InheritanceScope.ValueString(),
		"priority":         plan.Priority.ValueInt64(),
	}

	if !plan.Description.IsNull() && !plan.Description.IsUnknown() {
		requestBody["description"] = plan.Description.ValueString()
	}

	if !plan.RoleID.IsNull() && !plan.RoleID.IsUnknown() {
		requestBody["roleId"] = plan.RoleID.ValueString()
	}

	if !plan.UserID.IsNull() && !plan.UserID.IsUnknown() {
		requestBody["userId"] = plan.UserID.ValueString()
	}

	if !plan.TimeConstraints.IsNull() && !plan.TimeConstraints.IsUnknown() {
		var tc map[string]interface{}
		if err := json.Unmarshal([]byte(plan.TimeConstraints.ValueString()), &tc); err != nil {
			resp.Diagnostics.AddError("Invalid Time Constraints", "Could not parse time_constraints JSON: "+err.Error())
			return
		}
		requestBody["timeConstraints"] = tc
	}

	if !plan.Conditions.IsNull() && !plan.Conditions.IsUnknown() {
		var cond map[string]interface{}
		if err := json.Unmarshal([]byte(plan.Conditions.ValueString()), &cond); err != nil {
			resp.Diagnostics.AddError("Invalid Conditions", "Could not parse conditions JSON: "+err.Error())
			return
		}
		requestBody["conditions"] = cond
	}

	if !plan.ExpiresAt.IsNull() && !plan.ExpiresAt.IsUnknown() {
		requestBody["expiresAt"] = plan.ExpiresAt.ValueString()
	}

	// Call the API.
	body, err := r.client.doRequest("POST", "/api/policies", requestBody)
	if err != nil {
		resp.Diagnostics.AddError(
			"Error Creating Policy",
			"Could not create policy, unexpected error: "+err.Error(),
		)
		return
	}

	// Parse the response.
	var policy policyAPIResponse
	if err := json.Unmarshal(body, &policy); err != nil {
		resp.Diagnostics.AddError(
			"Error Parsing Response",
			"Could not parse policy creation response: "+err.Error(),
		)
		return
	}

	// Read back the full policy to get all fields.
	r.readPolicyIntoModel(ctx, policy.ID, &plan, &resp.Diagnostics)
	if resp.Diagnostics.HasError() {
		return
	}

	diags = resp.State.Set(ctx, plan)
	resp.Diagnostics.Append(diags...)

	tflog.Info(ctx, "Created policy", map[string]interface{}{
		"id":   policy.ID,
		"name": policy.Name,
	})
}

// Read refreshes the Terraform state with the latest data.
func (r *policyResource) Read(ctx context.Context, req resource.ReadRequest, resp *resource.ReadResponse) {
	var state policyResourceModel
	diags := req.State.Get(ctx, &state)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}

	tflog.Debug(ctx, "Reading policy", map[string]interface{}{
		"id": state.ID.ValueString(),
	})

	body, statusCode, err := r.client.doRequestWithStatus("GET", fmt.Sprintf("/api/policies/%s", state.ID.ValueString()), nil)
	if err != nil {
		if statusCode == 404 {
			tflog.Warn(ctx, "Policy not found, removing from state", map[string]interface{}{
				"id": state.ID.ValueString(),
			})
			resp.State.RemoveResource(ctx)
			return
		}
		resp.Diagnostics.AddError(
			"Error Reading Policy",
			"Could not read policy ID "+state.ID.ValueString()+": "+err.Error(),
		)
		return
	}

	var policy policyAPIResponse
	if err := json.Unmarshal(body, &policy); err != nil {
		resp.Diagnostics.AddError(
			"Error Parsing Response",
			"Could not parse policy read response: "+err.Error(),
		)
		return
	}

	r.mapPolicyResponseToModel(&policy, &state)

	diags = resp.State.Set(ctx, &state)
	resp.Diagnostics.Append(diags...)
}

// Update updates the resource and sets the updated Terraform state on success.
func (r *policyResource) Update(ctx context.Context, req resource.UpdateRequest, resp *resource.UpdateResponse) {
	var plan policyResourceModel
	diags := req.Plan.Get(ctx, &plan)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}

	var state policyResourceModel
	diags = req.State.Get(ctx, &state)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}

	policyID := state.ID.ValueString()

	tflog.Debug(ctx, "Updating policy", map[string]interface{}{
		"id": policyID,
	})

	// Build the update request body.
	requestBody := map[string]interface{}{}

	if plan.Name.ValueString() != state.Name.ValueString() {
		requestBody["name"] = plan.Name.ValueString()
	}

	if plan.Description.ValueString() != state.Description.ValueString() {
		requestBody["description"] = plan.Description.ValueString()
	}

	if plan.InheritanceScope.ValueString() != state.InheritanceScope.ValueString() {
		requestBody["inheritanceScope"] = plan.InheritanceScope.ValueString()
	}

	if plan.Priority.ValueInt64() != state.Priority.ValueInt64() {
		requestBody["priority"] = plan.Priority.ValueInt64()
	}

	if plan.IsActive.ValueBool() != state.IsActive.ValueBool() {
		requestBody["isActive"] = plan.IsActive.ValueBool()
	}

	// Handle time constraints.
	if !plan.TimeConstraints.IsNull() && !plan.TimeConstraints.IsUnknown() {
		var tc map[string]interface{}
		if err := json.Unmarshal([]byte(plan.TimeConstraints.ValueString()), &tc); err == nil {
			requestBody["timeConstraints"] = tc
		}
	}

	// Handle conditions.
	if !plan.Conditions.IsNull() && !plan.Conditions.IsUnknown() {
		var cond map[string]interface{}
		if err := json.Unmarshal([]byte(plan.Conditions.ValueString()), &cond); err == nil {
			requestBody["conditions"] = cond
		}
	}

	// Handle expiration.
	if !plan.ExpiresAt.IsNull() && !plan.ExpiresAt.IsUnknown() {
		requestBody["expiresAt"] = plan.ExpiresAt.ValueString()
	}

	// Call the API.
	_, err := r.client.doRequest("PUT", fmt.Sprintf("/api/policies/%s", policyID), requestBody)
	if err != nil {
		resp.Diagnostics.AddError(
			"Error Updating Policy",
			"Could not update policy ID "+policyID+": "+err.Error(),
		)
		return
	}

	// Read back the full policy.
	r.readPolicyIntoModel(ctx, policyID, &plan, &resp.Diagnostics)
	if resp.Diagnostics.HasError() {
		return
	}

	diags = resp.State.Set(ctx, plan)
	resp.Diagnostics.Append(diags...)

	tflog.Info(ctx, "Updated policy", map[string]interface{}{
		"id": policyID,
	})
}

// Delete deletes the resource and removes the Terraform state on success.
func (r *policyResource) Delete(ctx context.Context, req resource.DeleteRequest, resp *resource.DeleteResponse) {
	var state policyResourceModel
	diags := req.State.Get(ctx, &state)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}

	policyID := state.ID.ValueString()

	tflog.Debug(ctx, "Deleting policy", map[string]interface{}{
		"id": policyID,
	})

	_, statusCode, err := r.client.doRequestWithStatus("DELETE", fmt.Sprintf("/api/policies/%s", policyID), nil)
	if err != nil {
		if statusCode == 404 {
			tflog.Warn(ctx, "Policy already deleted/not found", map[string]interface{}{
				"id": policyID,
			})
			return
		}
		resp.Diagnostics.AddError(
			"Error Deleting Policy",
			"Could not delete policy ID "+policyID+": "+err.Error(),
		)
		return
	}

	tflog.Info(ctx, "Deleted policy", map[string]interface{}{
		"id": policyID,
	})
}

// ImportState imports an existing resource by its ID.
func (r *policyResource) ImportState(ctx context.Context, req resource.ImportStateRequest, resp *resource.ImportStateResponse) {
	resource.ImportStatePassthroughID(ctx, path.Root("id"), req, resp)
}

// readPolicyIntoModel reads a policy from the API and populates the model.
func (r *policyResource) readPolicyIntoModel(ctx context.Context, policyID string, model *policyResourceModel, diagnostics *diag.Diagnostics) {
	body, _, err := r.client.doRequestWithStatus("GET", fmt.Sprintf("/api/policies/%s", policyID), nil)
	if err != nil {
		diagnostics.AddError(
			"Error Reading Policy",
			"Could not read policy ID "+policyID+": "+err.Error(),
		)
		return
	}

	var policy policyAPIResponse
	if err := json.Unmarshal(body, &policy); err != nil {
		diagnostics.AddError(
			"Error Parsing Response",
			"Could not parse policy read response: "+err.Error(),
		)
		return
	}

	r.mapPolicyResponseToModel(&policy, model)
}

// mapPolicyResponseToModel maps an API response to the Terraform model.
func (r *policyResource) mapPolicyResponseToModel(policy *policyAPIResponse, model *policyResourceModel) {
	model.ID = types.StringValue(policy.ID)
	model.Name = types.StringValue(policy.Name)
	model.Description = types.StringValue(policy.Description)
	model.TenantID = types.StringValue(policy.TenantID)
	model.InheritanceScope = types.StringValue(policy.InheritanceScope)
	model.Resource = types.StringValue(policy.Resource)
	model.Action = types.StringValue(policy.Action)
	model.Effect = types.StringValue(policy.Effect)
	model.Priority = types.Int64Value(int64(policy.Priority))
	model.IsActive = types.BoolValue(policy.IsActive)
	model.CreatedAt = types.StringValue(policy.CreatedAt)
	model.UpdatedAt = types.StringValue(policy.UpdatedAt)
	model.CreatedByUserID = types.StringValue(policy.CreatedByUserID)

	if policy.RoleID != nil {
		model.RoleID = types.StringValue(*policy.RoleID)
	} else {
		model.RoleID = types.StringNull()
	}

	if policy.UserID != nil {
		model.UserID = types.StringValue(*policy.UserID)
	} else {
		model.UserID = types.StringNull()
	}

	if policy.TimeConstraints != nil {
		tcJSON, _ := json.Marshal(policy.TimeConstraints)
		model.TimeConstraints = types.StringValue(string(tcJSON))
	} else {
		model.TimeConstraints = types.StringNull()
	}

	if policy.Conditions != nil {
		condJSON, _ := json.Marshal(policy.Conditions)
		model.Conditions = types.StringValue(string(condJSON))
	} else {
		model.Conditions = types.StringNull()
	}

	if policy.ExpiresAt != nil {
		model.ExpiresAt = types.StringValue(*policy.ExpiresAt)
	} else {
		model.ExpiresAt = types.StringNull()
	}
}
