// Shared types matching backend contracts
export type JobStatus = 'Created' | 'Queued' | 'Processing' | 'WaitingForApiKey' | 'Completed' | 'Failed' | 'Cancelled';

export interface ApiError {
  message: string;
  code?: string;
}
