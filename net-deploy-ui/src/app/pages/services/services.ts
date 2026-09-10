import { Component, inject, OnInit, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TranslateModule } from '@ngx-translate/core';
import { ServicesMonitorService } from '../../services/services-monitor.service';
import { SettingsService } from '../../services/settings.service';
import { EnvConfigsService } from '../../services/env-configs.service';
import { DeployService } from '../../services/deploy.service';
import { ServiceStatus, ServiceDefinition, ServiceEnvironmentConfig, VpsSettings, EnvConfigSet } from '../../models/api-models';

@Component({
  selector: 'app-services',
  standalone: true,
  imports: [CommonModule, FormsModule, TranslateModule],
  templateUrl: './services.html',
  styleUrl: './services.less'
})
export class ServicesComponent implements OnInit {
  private servicesSvc = inject(ServicesMonitorService);
  private settingsSvc = inject(SettingsService);
  private configSvc = inject(EnvConfigsService);
  private deploySvc = inject(DeployService);

  services = signal<ServiceStatus[]>([]);
  loading = signal<boolean>(true);
  allConfigSets = signal<EnvConfigSet[]>([]);
  environments = signal<VpsSettings[]>([]);

  targetEnvId: string | null = null;
  activeActionServiceId: string | null = null;
  isModalOpen = false;

  isAddModalOpen = false;
  isConfigLookupOpen = false;
  configSearchQuery = '';

  selectedService: ServiceStatus | null = null;
  newService: Partial<ServiceDefinition> = this.resetNewService();
  activeEnvTab: string | null = 'general';

  filteredConfigSets = computed(() => {
    const q = this.configSearchQuery.toLowerCase();
    return this.allConfigSets().filter(s => s.name.toLowerCase().includes(q));
  });

  groupedServices = computed(() => {
    const groups = new Map<string, ServiceStatus[]>();

    for (const service of this.services()) {
      const key = (service.group || '').trim() || '__ungrouped__';
      const items = groups.get(key) ?? [];
      items.push(service);
      groups.set(key, items);
    }

    return Array.from(groups.entries())
      .sort(([left], [right]) => {
        if (left === '__ungrouped__') return 1;
        if (right === '__ungrouped__') return -1;
        return left.localeCompare(right);
      })
      .map(([key, services]) => ({
        key,
        title: key === '__ungrouped__' ? 'Ungrouped' : key,
        services
      }));
  });

  collapsedGroups = signal<Set<string>>(new Set());

  ngOnInit() {
    try {
      const savedCollapsed = localStorage.getItem('servicesPageCollapsedGroups');
      if (savedCollapsed) {
        const parsed = JSON.parse(savedCollapsed);
        if (Array.isArray(parsed)) {
          this.collapsedGroups.set(new Set(parsed));
        }
      }
    } catch (e) {
      console.error('Failed to load collapsed groups from localStorage', e);
    }

    this.loadData();
    this.loadConfigSets();
    this.loadEnvironments();
  }

  toggleGroupCollapse(groupKey: string) {
    const current = new Set(this.collapsedGroups());
    if (current.has(groupKey)) {
      current.delete(groupKey);
    } else {
      current.add(groupKey);
    }
    this.collapsedGroups.set(current);
    try {
      localStorage.setItem('servicesPageCollapsedGroups', JSON.stringify(Array.from(current)));
    } catch (e) {
      console.error('Failed to save collapsed groups to localStorage', e);
    }
  }

  isGroupCollapsed(groupKey: string): boolean {
    return this.collapsedGroups().has(groupKey);
  }

  loadData() {
    this.loading.set(true);
    this.servicesSvc.getAll(this.targetEnvId).subscribe({
      next: (data) => {
        this.services.set(data);
        this.loading.set(false);
        this.checkHeartbeats();
      },
      error: () => this.loading.set(false)
    });
  }

  checkHeartbeats() {
    if (!this.targetEnvId) {
      this.services.update(list => list.map(service => ({
        ...service,
        isChecking: false,
        hbStatus: 'Unknown'
      })));
      return;
    }

    let hasHeartbeat = false;
    this.services.update(list => list.map(service => {
      const env = service.environments?.find(e => e.environmentId === this.targetEnvId);
      const shouldCheck = !!env?.heartbeatUrl?.trim();
      if (shouldCheck) hasHeartbeat = true;

      return {
        ...service,
        isChecking: shouldCheck,
        hbStatus: shouldCheck ? 'Checking' : 'Unknown'
      };
    }));

    if (!hasHeartbeat) return;

    this.servicesSvc.getHeartbeats(this.targetEnvId).subscribe({
      next: (results) => {
        const byServiceId = new Map(results.map(result => [result.serviceId, result.status]));
        this.services.update(list => list.map(service => {
          const env = service.environments?.find(e => e.environmentId === this.targetEnvId);
          const shouldCheck = !!env?.heartbeatUrl?.trim();

          return {
            ...service,
            isChecking: false,
            hbStatus: shouldCheck ? (byServiceId.get(service.id!) ?? 'Unknown') : 'Unknown'
          };
        }));
      },
      error: () => {
        this.services.update(list => list.map(service => ({
          ...service,
          isChecking: false,
          hbStatus: service.isChecking ? 'Stopped' : 'Unknown'
        })));
      }
    });
  }

  loadConfigSets() {
    this.configSvc.getAll().subscribe(sets => this.allConfigSets.set(sets));
  }

  loadEnvironments() {
    this.settingsSvc.getSettings().subscribe(s => {
      const envs = s.vpsEnvironments || [];
      this.environments.set(envs);
      
      const savedEnvId = localStorage.getItem('lastTargetEnvId');
      const exists = envs.some(e => e.id === savedEnvId);

      if (savedEnvId && exists) {
        this.targetEnvId = savedEnvId;
      } else if (envs.length > 0) {
        this.targetEnvId = envs[0].id || null;
      }

      this.loadData();
    });
  }

  onTargetEnvChange(id: string | null) {
    if (id) {
      localStorage.setItem('lastTargetEnvId', id);
      this.loadData();
    }
  }

  getCurrentVersion(service: ServiceStatus) {
    if (!service.environments || !this.targetEnvId) return null;
    return service.environments.find(e => e.environmentId === this.targetEnvId)?.currentVersion || null;
  }

  getRepoCommitUrl(repoUrl: string, commitHash: string): string | null {
    const normalizedRepoUrl = this.normalizeRepoUrl(repoUrl);
    if (!normalizedRepoUrl || !commitHash) return null;

    try {
      const url = new URL(normalizedRepoUrl);
      const encodedCommitHash = encodeURIComponent(commitHash);

      if (url.hostname.includes('gitlab')) {
        return `${normalizedRepoUrl}/-/commit/${encodedCommitHash}`;
      }

      if (url.hostname.includes('bitbucket')) {
        return `${normalizedRepoUrl}/commits/${encodedCommitHash}`;
      }

      return `${normalizedRepoUrl}/commit/${encodedCommitHash}`;
    } catch {
      return null;
    }
  }

  runAction(serviceId: string, action: string) {
    if (!this.targetEnvId || this.activeActionServiceId) return;
    this.activeActionServiceId = serviceId;

    this.deploySvc.serviceAction(serviceId, this.targetEnvId, action).subscribe({
      next: (log) => {
        // We could show these in a console component if we wanted
        console.log(`[Action: ${action}]`, log.message);
      },
      complete: () => {
        // Refresh statuses after action completes
        setTimeout(() => {
          this.loadData();
          this.activeActionServiceId = null;
        }, 1200);
      },
      error: (err) => {
        this.activeActionServiceId = null;
        alert('Action failed: ' + err.message);
      }
    });
  }


  allEnvironments(): VpsSettings[] {
    return this.environments();
  }

  getEnvironmentName(id: string): string {
    return this.environments().find(e => e.id === id)?.name || id;
  }

  getEnvironmentTag(id: string): string {
    return this.environments().find(e => e.id === id)?.environmentTag || '';
  }

  openAddModal() {
    this.newService = this.resetNewService();
    this.isAddModalOpen = true;
  }

  closeAddModal() {
    this.isAddModalOpen = false;
  }

  addService() {
    if (!this.newService.name || !this.newService.repoUrl) return;
    this.servicesSvc.create(this.newService as ServiceDefinition).subscribe({
      next: () => {
        this.closeAddModal();
        this.loadData();
      }
    });
  }

  openEditModal(service: ServiceStatus) {
    this.selectedService = JSON.parse(JSON.stringify(service)); // Deep copy
    if (!this.selectedService!.environments) this.selectedService!.environments = [];

    // Ensure all environments have a config set up
    this.allEnvironments().forEach(env => {
      let cfg = this.selectedService!.environments.find(e => e.environmentId === env.id);
      if (env.id && !cfg) {
        cfg = {
          environmentId: env.id,
          deployTargetPath: '',
          heartbeatUrl: '',
          defaultBranch: 'main',
          configSetIds: []
        };
        this.selectedService!.environments.push(cfg);
      }
      if (cfg && !cfg.configSetIds) {
        cfg.configSetIds = [];
      }
    });

    this.activeEnvTab = 'general';
    this.isModalOpen = true;
  }

  closeModal() {
    this.isModalOpen = false;
    this.selectedService = null;
    this.activeEnvTab = 'general';
  }

  updateService() {
    if (!this.selectedService || !this.selectedService.id) return;
    this.servicesSvc.update(this.selectedService.id, this.selectedService).subscribe({
      next: () => {
        this.closeModal();
        this.loadData();
      }
    });
  }

  deleteService(id: string) {
    if (!confirm('Are you sure you want to delete this service definition?')) return;
    this.servicesSvc.delete(id).subscribe({
      next: () => {
        this.closeModal();
        this.loadData();
      }
    });
  }

  private resetNewService(): Partial<ServiceDefinition> {
    return { 
      name: '', 
      group: '',
      repoUrl: '', 
      projectPath: '', 
      iisSiteName: '', 
      serviceType: 'WebApi', 
      compileSingleFile: false,
      dockerfilePath: 'Dockerfile',
      dockerComposePath: 'docker-compose.yml',
      dockerContainerName: '',
      dockerComposeServiceName: '',
      dockerComposeProjectName: '',
      environments: [] 
    };
  }

  private normalizeRepoUrl(repoUrl: string): string | null {
    if (!repoUrl) return null;

    const trimmed = repoUrl.trim();
    const blobIndex = trimmed.indexOf('/blob/');
    const treeIndex = trimmed.indexOf('/tree/');
    const splitIndex = blobIndex >= 0 ? blobIndex : treeIndex;
    const repoOnly = splitIndex >= 0 ? trimmed.substring(0, splitIndex) : trimmed;
    const withoutGitSuffix = repoOnly.replace(/\.git$/i, '');

    return withoutGitSuffix || null;
  }

  selectEnvTab(tab?: string) {
    if (!tab) return;
    this.activeEnvTab = tab;
    if (tab !== 'general' && this.selectedService) {
      this.getEnvConfig(tab); // Ensure config object exists in environments array
    }
  }

  get activeEnvConfig(): ServiceEnvironmentConfig | null {
    if (!this.selectedService || !this.activeEnvTab || this.activeEnvTab === 'general') return null;
    return this.selectedService.environments?.find(e => e.environmentId === this.activeEnvTab) || null;
  }

  getEnvConfig(envId: string): ServiceEnvironmentConfig {
    if (!this.selectedService) return {} as any;
    if (!this.selectedService.environments) this.selectedService.environments = [];
    let config = this.selectedService.environments.find(e => e.environmentId === envId);
    if (!config && envId) {
      config = {
        environmentId: envId,
        deployTargetPath: '',
        dockerPort: 8080,
        dockerReplicas: 2,
        dockerParallelism: 1,
        dockerDrainSeconds: 10,
        enableZeroDowntime: true,
        dockerEnvironmentComposePath: '',
        heartbeatUrl: '',
        defaultBranch: 'main',
        configSetIds: []
      };
      this.selectedService.environments.push(config);
    }
    if (config && !config.configSetIds) {
      config.configSetIds = [];
    }
    return config || {
      environmentId: envId,
      deployTargetPath: '',
      heartbeatUrl: '',
      defaultBranch: 'main',
      configSetIds: []
    };
  }

  toggleConfigSet(envId: string, setId: string) {
    if (!envId || envId === 'general' || !this.selectedService) return;

    const environments = this.selectedService.environments ?? [];
    const existingConfig = environments.find(environment => environment.environmentId === envId);
    const config = existingConfig ?? this.createEnvironmentConfig(envId);
    const currentIds = [...(config.configSetIds ?? [])];
    const index = currentIds.indexOf(setId);
    if (index > -1) {
      currentIds.splice(index, 1);
    } else {
      currentIds.push(setId);
    }

    const updatedConfig = { ...config, configSetIds: currentIds };
    const updatedEnvironments = existingConfig
      ? environments.map(environment => environment.environmentId === envId ? updatedConfig : environment)
      : [...environments, updatedConfig];

    this.selectedService = {
      ...this.selectedService,
      environments: updatedEnvironments
    };
  }

  isDockerEnvironment(id: string | null): boolean {
    if (!id || id === 'general') return false;
    const serverType = this.environments().find(environment => environment.id === id)?.serverType;
    return serverType === 'LinuxDocker' || serverType === 'WindowsDocker';
  }

  isConfigSetSelected(envId: string, setId: string): boolean {
    if (!envId || envId === 'general') return false;
    const cfg = this.getEnvConfig(envId);
    return cfg.configSetIds ? cfg.configSetIds.includes(setId) : false;
  }

  getSelectedConfigSets(envId: string): EnvConfigSet[] {
    if (!envId || envId === 'general') return [];
    const ids = this.getEnvConfig(envId).configSetIds || [];
    return this.allConfigSets().filter(s => ids.includes(s.id!));
  }

  openConfigLookup() {
    this.configSearchQuery = '';
    this.isConfigLookupOpen = true;
  }

  private createEnvironmentConfig(environmentId: string): ServiceEnvironmentConfig {
    return {
      environmentId,
      deployTargetPath: '',
      dockerPort: 8080,
      dockerReplicas: 2,
      dockerParallelism: 1,
      dockerDrainSeconds: 10,
      enableZeroDowntime: true,
      dockerEnvironmentComposePath: '',
      heartbeatUrl: '',
      defaultBranch: 'main',
      configSetIds: []
    };
  }

  closeConfigLookup() {
    this.isConfigLookupOpen = false;
  }
}
