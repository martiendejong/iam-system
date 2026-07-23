export interface WebhookSubscription {
  id: string;
  name: string;
  url: string;
  hasSecret: boolean;
  tenantId: string;
  tenantName?: string;
  createdByUserId?: string;
  events: string[];
  headers?: Record<string, string>;
  isActive: boolean;
  contentType: string;
  maxRetries: number;
  timeoutSeconds: number;
  createdAt: string;
  updatedAt: string;
  totalDeliveries: number;
  successfulDeliveries: number;
  failedDeliveries: number;
  lastDeliveryAt?: string;
  lastSuccessAt?: string;
  lastFailureAt?: string;
}

export interface WebhookDelivery {
  id: string;
  subscriptionId: string;
  eventType: string;
  httpStatusCode: number;
  responseBody?: string;
  attemptNumber: number;
  durationMs: number;
  success: boolean;
  error?: string;
  createdAt: string;
}

export interface EventTypeInfo {
  name: string;
  category: string;
}

export interface EventTypesResponse {
  eventTypes: EventTypeInfo[];
  wildcardSupported: boolean;
  wildcardExamples: string[];
}

export interface CreateWebhookRequest {
  name: string;
  url: string;
  secret?: string;
  tenantId: string;
  events: string[];
  headers?: Record<string, string>;
  isActive?: boolean;
  contentType?: string;
  maxRetries?: number;
  timeoutSeconds?: number;
}

export interface UpdateWebhookRequest {
  name?: string;
  url?: string;
  events?: string[];
  isActive?: boolean;
}
