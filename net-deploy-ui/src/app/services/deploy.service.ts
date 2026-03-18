import { Injectable, NgZone, signal, WritableSignal } from '@angular/core';
import { ApiService } from './api.service';
import { DeployLogEntry } from '../models/api-models';
import { Observable, Subject } from 'rxjs';
import { HttpClient } from '@angular/common/http';

@Injectable({
  providedIn: 'root'
})
export class DeployService extends ApiService {
  // State signals that persist across page navigation
  deploying = signal<boolean>(false);
  logs = signal<DeployLogEntry[]>([]);
  deploymentProgress = signal<Record<string, { compiled: string; deployed: string; heartbeat: string; buildTime: string; buildStartTime?: number }>>({});
  elapsedTime = signal<string>('00:00');
  failedServiceIds = signal<string[]>([]);
  currentSessionId = signal<string | null>(null);
  isPaused = signal<boolean>(false);
  
  // Strategy options
  deployPull = signal<boolean>(true);
  deployBuild = signal<boolean>(true);
  deployTransfer = signal<boolean>(true);

  private timerInterval: any;
  private startTime: number = 0;

  constructor(private zone: NgZone, http: HttpClient) {
    super();
    this.http = http;
  }

  startDeployment(
    configs: { serviceId: string, branch: string }[], 
    environmentId: string | null, 
    forceClean: boolean, 
    pull: boolean, 
    build: boolean, 
    deploy: boolean
  ) {
    this.deploying.set(true);
    this.logs.set([]);
    this.failedServiceIds.set([]);
    this.isPaused.set(false);
    
    // Initialize progress
    const initialProgress: any = {};
    configs.forEach(c => {
      initialProgress[c.serviceId] = { compiled: 'pending', deployed: 'pending', heartbeat: 'pending', buildTime: '' };
    });
    this.deploymentProgress.set(initialProgress);
    
    this.startTimer();

    this.deploy(configs, environmentId, forceClean, pull, build, deploy).subscribe({
      next: (entry: DeployLogEntry) => {
        if (entry.level === 'SESSION_ID') {
          this.currentSessionId.set(entry.message);
          return;
        }

        this.logs.update(prev => [...prev, entry]);

        if (entry.serviceId) {
          this.updateServiceProgress(entry.serviceId, entry.message, entry.level);
        }

        if (entry.level === 'ERROR' && entry.serviceId) {
          this.failedServiceIds.update(fails => [...new Set([...fails, entry.serviceId!])]);
        }
      },
      complete: () => {
        this.deploying.set(false);
        this.currentSessionId.set(null);
        this.isPaused.set(false);
        this.stopTimer();
      },
      error: (err: any) => {
        this.deploying.set(false);
        this.currentSessionId.set(null);
        this.isPaused.set(false);
        this.stopTimer();
        this.logs.update(prev => [...prev, {
          sessionId: 'client',
          level: 'ERROR',
          message: 'Connection to server failed or dropped randomly.',
          timestamp: new Date().toISOString()
        }]);
      }
    });
  }

  private updateServiceProgress(serviceId: string, message: string, level: string) {
    const progress = { ...this.deploymentProgress() };
    if (!progress[serviceId]) return;
    const row = { ...progress[serviceId] };

    if (message.includes('🔨 [Prep] Building')) {
      row.compiled = 'process';
      row.buildStartTime = Date.now();
    }
    if (message.includes('✅ [Prep] Prepared') || message.includes('⏭️ [Prep] Build output already exists') || message.includes('❌ Preparation failed')) {
      row.compiled = (message.includes('✅ [Prep] Prepared') || message.includes('⏭️ [Prep] Build output already exists')) ? 'success' : 'error';
      if (row.buildStartTime) {
        row.buildTime = ((Date.now() - row.buildStartTime) / 1000).toFixed(1) + 's';
      }
    }
    if (message.includes('🚀 Uploading files') || message.includes('📂 Copying files')) row.deployed = 'process';
    if (message.includes('✅ Files uploaded') || message.includes('✅ Files copied')) row.deployed = 'success';
    if (message.includes('❌ Failed to transfer')) row.deployed = 'error';
    if (message.includes('💓 Checking heartbeat')) row.heartbeat = 'process';
    if (message.includes('✅ Heartbeat OK')) row.heartbeat = 'success';
    if (message.includes('⚠️ Heartbeat returned error') || message.includes('❌ Heartbeat failed')) row.heartbeat = 'error';

    if (level === 'ERROR') {
      if (row.compiled === 'process' || row.compiled === 'pending') row.compiled = 'error';
      if (row.deployed === 'process' || row.deployed === 'pending') row.deployed = 'error';
      if (row.heartbeat === 'process' || row.heartbeat === 'pending') row.heartbeat = 'error';
    }

    progress[serviceId] = row;
    this.deploymentProgress.set(progress);
  }

  private startTimer() {
    this.startTime = Date.now();
    this.elapsedTime.set('00:00');
    if (this.timerInterval) clearInterval(this.timerInterval);
    this.timerInterval = setInterval(() => {
      const seconds = Math.floor((Date.now() - this.startTime) / 1000);
      const mins = Math.floor(seconds / 60);
      const secs = seconds % 60;
      this.elapsedTime.set(`${mins.toString().padStart(2, '0')}:${secs.toString().padStart(2, '0')}`);
    }, 1000);
  }

  private stopTimer() {
    if (this.timerInterval) {
      clearInterval(this.timerInterval);
      this.timerInterval = null;
    }
  }

  // Original API methods remain below...

  /**
   * Triggers a deploy and returns an Observable that streams log entries
   * via Server-Sent Events (SSE).
   */
  deploy(services: { serviceId: string, branch?: string }[], environmentId?: string | null, forceClean: boolean = false, pull: boolean = true, build: boolean = true, deploy: boolean = true): Observable<DeployLogEntry> {
    return this.streamLogs(`${this.baseUrl}/deploy`, { services, environmentId, forceClean, pull, build, deploy });
  }

  serviceAction(serviceId: string, environmentId: string, action: string): Observable<DeployLogEntry> {
    return this.streamLogs(`${this.baseUrl}/deploy/service-action`, { serviceId, environmentId, action });
  }

  stop(sessionId: string): Observable<any> {
    return this.http!.post(`${this.baseUrl}/deploy/stop/${sessionId}`, {});
  }

  pause(sessionId: string): Observable<any> {
    return this.http!.post(`${this.baseUrl}/deploy/pause/${sessionId}`, {});
  }

  resume(sessionId: string): Observable<any> {
    return this.http!.post(`${this.baseUrl}/deploy/resume/${sessionId}`, {});
  }

  private streamLogs(url: string, body: any): Observable<DeployLogEntry> {
    const subject = new Subject<DeployLogEntry>();

    fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    }).then(async response => {
      if (!response.body) throw new Error('No body returned from server.');
      
      const reader = response.body.getReader();
      const decoder = new TextDecoder('utf-8');
      
      let buffer = '';
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;

        buffer += decoder.decode(value, { stream: true });
        const lines = buffer.split('\n\n');
        buffer = lines.pop() || '';

        for (const line of lines) {
          if (line.startsWith('data: ')) {
            const dataStr = line.replace('data: ', '').trim();
            if (dataStr) {
              const entry = JSON.parse(dataStr) as DeployLogEntry;
              this.zone.run(() => {
                subject.next(entry);
                if (entry.level === 'DONE') subject.complete();
              });
            }
          }
        }
      }
    }).catch(err => {
      this.zone.run(() => subject.error(err));
    });

    return subject.asObservable();
  }


  getSessions(count = 10): Observable<string[]> {
    return this.http!.get<string[]>(`${this.baseUrl}/Deploy/sessions?count=${count}`);
  }

  getSessionsPaged(skip = 0, limit = 20): Observable<SessionSummary[]> {
    return this.http!.get<SessionSummary[]>(`${this.baseUrl}/Deploy/sessions-paged?skip=${skip}&limit=${limit}`);
  }

  getLogs(sessionId: string): Observable<DeployLogEntry[]> {
    return this.http!.get<DeployLogEntry[]>(`${this.baseUrl}/Deploy/logs/${sessionId}`);
  }
}

export interface SessionSummary {
  sessionId: string;
  timestamp: string;
  hasErrors: boolean;
}
