export interface AuditEvent {
  id: string;
  eventType: string;
  userId?: string;
  userName?: string;
  tenantId?: string;
  tenantName?: string;
  resourceType?: string;
  resourceId?: string;
  action: string;
  details?: string;
  ipAddress?: string;
  userAgent?: string;
  success: boolean;
  riskScore?: number;
  createdAt: string;
}

export interface AuditStatistics {
  totalEvents: number;
  eventsByType: Record<string, number>;
  eventsByDay: { date: string; count: number }[];
  failedEvents: number;
  highRiskEvents: number;
}

export interface ComplianceReport {
  id: string;
  framework: string;
  generatedAt: string;
  overallScore: number;
  findings: ComplianceFinding[];
}

export interface ComplianceFinding {
  category: string;
  status: 'pass' | 'fail' | 'warning';
  description: string;
  recommendation?: string;
}

export interface AuditFilter {
  startDate?: string;
  endDate?: string;
  eventType?: string;
  userId?: string;
  tenantId?: string;
  success?: boolean;
  search?: string;
}
