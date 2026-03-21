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
  id: string;
  name: string;
  host: string;
  username: string;
  password?: string;
  port: number;
  isLocal: boolean;
  environmentTag: string;
  sharedVariables?: EnvVariable[];
  sharedFileRenames?: FileRename[];
}

export interface EnvConfigSet {
  id?: string;
  name: string;
  sourceFileName: string;
  targetFileName: string;
  variables: EnvVariable[];
}

export interface ServiceDefinition {
  id?: string;
  name: string;
  group: string;
  repoUrl: string;
  projectPath: string;
  iisSiteName: string;
  serviceType: 'WebApi' | 'Mvc' | 'WindowsService' | 'Angular' | 'React';
  compileSingleFile: boolean;
  lastDeployed?: string | Date;
  environments: ServiceEnvironmentConfig[];
  order?: number;
  _saved?: boolean; // UI state only
}

export interface ServiceEnvironmentConfig {
  environmentId: string;
  deployTargetPath: string;
  heartbeatUrl: string;
  defaultBranch: string;
  configSetIds: string[];
}

export interface EnvVariable {
  key: string;
  value: string;
}

export interface FileRename {
  sourceFileName: string;
  targetFileName: string;
}

export interface ServiceStatus extends ServiceDefinition {
  status: 'Running' | 'Stopped' | 'Error' | 'Unknown';
  isChecking?: boolean;
  hbStatus?: 'Running' | 'Stopped' | 'Unknown' | 'Checking';
}

export interface ServiceHeartbeatStatus {
  serviceId: string;
  status: 'Running' | 'Stopped' | 'Unknown';
  httpStatusCode?: number | null;
}

export interface DeployLogEntry {
  id?: string;
  sessionId: string;
  timestamp: string | Date;
  level: 'INFO' | 'SUCCESS' | 'ERROR' | 'WARNING' | 'DONE' | 'SESSION_ID';
  message: string;
  serviceId?: string;
}
