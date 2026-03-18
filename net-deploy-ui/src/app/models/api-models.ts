export interface AppSettings {
  id?: string;
  git: GitSettings;
  vpsEnvironments: VpsSettings[];
}

export interface GitSettings {
  token: string;
  localBaseDir: string;
}

export interface VpsSettings {
  id?: string;
  name: string;
  host: string;
  username: string;
  password?: string;
  port: number;
  isLocal: boolean;
}

export interface ServiceDefinition {
  id?: string;
  name: string;
  repoUrl: string;
  projectPath: string;
  iisSiteName: string;
  serviceType: 'WebApi' | 'Mvc' | 'WindowsService' | 'Angular' | 'React';
  enabled: boolean;
  branch: string;
  compileSingleFile: boolean;
  heartbeatUrl: string;
  deployTargetPath: string;
  lastDeployed?: string | Date;
  _saved?: boolean; // UI state only
}

export interface ServiceStatus extends ServiceDefinition {
  status: 'Running' | 'Stopped' | 'Error' | 'Unknown';
}

export interface DeployLogEntry {
  id?: string;
  sessionId: string;
  timestamp: string | Date;
  level: 'INFO' | 'SUCCESS' | 'ERROR' | 'WARNING' | 'DONE';
  message: string;
  serviceId?: string;
}
