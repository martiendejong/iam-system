import fs from 'fs-extra';
import path from 'path';

interface GeneratorConfig {
  apiUrl: string;
  typescript: boolean; // Angular is always TypeScript, but kept for consistency
}

export async function generateAngularAuth(cwd: string, config: GeneratorConfig): Promise<void> {
  const { apiUrl } = config;

  // Generate IAM service
  const servicesDir = path.join(cwd, 'src', 'app', 'services');
  await fs.ensureDir(servicesDir);

  const iamServiceContent = generateIamService(apiUrl);
  await fs.writeFile(path.join(servicesDir, 'iam.service.ts'), iamServiceContent);

  // Generate auth guard
  const guardsDir = path.join(cwd, 'src', 'app', 'guards');
  await fs.ensureDir(guardsDir);

  const authGuardContent = generateAuthGuard();
  await fs.writeFile(path.join(guardsDir, 'auth.guard.ts'), authGuardContent);

  // Generate auth interceptor
  const interceptorsDir = path.join(cwd, 'src', 'app', 'interceptors');
  await fs.ensureDir(interceptorsDir);

  const authInterceptorContent = generateAuthInterceptor();
  await fs.writeFile(path.join(interceptorsDir, 'auth.interceptor.ts'), authInterceptorContent);

  // Generate example login component
  const loginDir = path.join(cwd, 'src', 'app', 'login');
  await fs.ensureDir(loginDir);

  const loginComponentContent = generateLoginComponent();
  await fs.writeFile(path.join(loginDir, 'login.component.ts'), loginComponentContent);

  const loginTemplateContent = generateLoginTemplate();
  await fs.writeFile(path.join(loginDir, 'login.component.html'), loginTemplateContent);

  const loginStyleContent = generateLoginStyle();
  await fs.writeFile(path.join(loginDir, 'login.component.css'), loginStyleContent);
}

function generateIamService(apiUrl: string): string {
  return `import { Injectable } from '@angular/core';
import { BehaviorSubject, Observable } from 'rxjs';
import { IamAuthClient, UserDto, LoginResponse } from '@iam-system/sdk';

@Injectable({
  providedIn: 'root'
})
export class IamService {
  private userSubject: BehaviorSubject<UserDto | null>;
  private accessTokenSubject: BehaviorSubject<string | null>;
  private iamClient: IamAuthClient;

  public user$: Observable<UserDto | null>;
  public accessToken$: Observable<string | null>;
  public isAuthenticated$: Observable<boolean>;

  constructor() {
    this.userSubject = new BehaviorSubject<UserDto | null>(null);
    this.accessTokenSubject = new BehaviorSubject<string | null>(null);

    this.user$ = this.userSubject.asObservable();
    this.accessToken$ = this.accessTokenSubject.asObservable();
    this.isAuthenticated$ = new BehaviorSubject<boolean>(false);

    this.iamClient = new IamAuthClient({
      apiBaseUrl: '${apiUrl}',
      autoRefreshTokens: false,
      onTokenRefreshed: (newAccessToken: string, newRefreshToken?: string) => {
        this.accessTokenSubject.next(newAccessToken);
        if (newRefreshToken) {
          localStorage.setItem('refreshToken', newRefreshToken);
        }
      },
      onAuthenticationFailed: () => {
        this.userSubject.next(null);
        this.accessTokenSubject.next(null);
        localStorage.removeItem('refreshToken');
      }
    });

    // Try to restore session on initialization
    this.restoreSession();
  }

  private async restoreSession(): Promise<void> {
    const refreshToken = localStorage.getItem('refreshToken');
    if (refreshToken) {
      try {
        const response = await this.iamClient.refreshToken(refreshToken);
        this.userSubject.next(response.user);
        this.accessTokenSubject.next(response.accessToken);
        if (response.refreshToken) {
          localStorage.setItem('refreshToken', response.refreshToken);
        }
      } catch {
        localStorage.removeItem('refreshToken');
      }
    }
  }

  public get currentUser(): UserDto | null {
    return this.userSubject.value;
  }

  public get currentAccessToken(): string | null {
    return this.accessTokenSubject.value;
  }

  public get isAuthenticated(): boolean {
    return !!this.userSubject.value;
  }

  public async login(email: string, password: string): Promise<LoginResponse> {
    const response = await this.iamClient.login(email, password);
    this.userSubject.next(response.user);
    this.accessTokenSubject.next(response.accessToken);
    if (response.refreshToken) {
      localStorage.setItem('refreshToken', response.refreshToken);
    }
    return response;
  }

  public async register(
    email: string,
    password: string,
    firstName: string,
    lastName: string
  ): Promise<LoginResponse> {
    const response = await this.iamClient.register(email, password, firstName, lastName);
    this.userSubject.next(response.user);
    this.accessTokenSubject.next(response.accessToken);
    if (response.refreshToken) {
      localStorage.setItem('refreshToken', response.refreshToken);
    }
    return response;
  }

  public async logout(): Promise<void> {
    const refreshToken = localStorage.getItem('refreshToken');
    if (refreshToken) {
      try {
        await this.iamClient.logout(refreshToken);
      } catch {
        // Ignore logout errors
      }
    }
    this.userSubject.next(null);
    this.accessTokenSubject.next(null);
    localStorage.removeItem('refreshToken');
  }

  public async refreshToken(): Promise<LoginResponse> {
    const refreshToken = localStorage.getItem('refreshToken');
    if (!refreshToken) {
      throw new Error('No refresh token available');
    }
    const response = await this.iamClient.refreshToken(refreshToken);
    this.userSubject.next(response.user);
    this.accessTokenSubject.next(response.accessToken);
    if (response.refreshToken) {
      localStorage.setItem('refreshToken', response.refreshToken);
    }
    return response;
  }
}
`;
}

function generateAuthGuard(): string {
  return `import { Injectable } from '@angular/core';
import { Router, CanActivate, ActivatedRouteSnapshot, RouterStateSnapshot } from '@angular/router';
import { IamService } from '../services/iam.service';

@Injectable({
  providedIn: 'root'
})
export class AuthGuard implements CanActivate {
  constructor(
    private router: Router,
    private iamService: IamService
  ) {}

  canActivate(route: ActivatedRouteSnapshot, state: RouterStateSnapshot): boolean {
    if (this.iamService.isAuthenticated) {
      return true;
    }

    // Not logged in, redirect to login page with return URL
    this.router.navigate(['/login'], { queryParams: { returnUrl: state.url } });
    return false;
  }
}
`;
}

function generateAuthInterceptor(): string {
  return `import { Injectable } from '@angular/core';
import {
  HttpRequest,
  HttpHandler,
  HttpEvent,
  HttpInterceptor,
  HttpErrorResponse
} from '@angular/common/http';
import { Observable, throwError, BehaviorSubject } from 'rxjs';
import { catchError, filter, take, switchMap } from 'rxjs/operators';
import { IamService } from '../services/iam.service';

@Injectable()
export class AuthInterceptor implements HttpInterceptor {
  private isRefreshing = false;
  private refreshTokenSubject: BehaviorSubject<string | null> = new BehaviorSubject<string | null>(null);

  constructor(private iamService: IamService) {}

  intercept(request: HttpRequest<unknown>, next: HttpHandler): Observable<HttpEvent<unknown>> {
    // Add access token to request if available
    const accessToken = this.iamService.currentAccessToken;
    if (accessToken) {
      request = this.addToken(request, accessToken);
    }

    return next.handle(request).pipe(
      catchError((error: HttpErrorResponse) => {
        if (error.status === 401 && accessToken) {
          return this.handle401Error(request, next);
        }
        return throwError(() => error);
      })
    );
  }

  private addToken(request: HttpRequest<unknown>, token: string): HttpRequest<unknown> {
    return request.clone({
      setHeaders: {
        Authorization: \`Bearer \${token}\`
      }
    });
  }

  private handle401Error(request: HttpRequest<unknown>, next: HttpHandler): Observable<HttpEvent<unknown>> {
    if (!this.isRefreshing) {
      this.isRefreshing = true;
      this.refreshTokenSubject.next(null);

      return new Observable<HttpEvent<unknown>>((observer) => {
        this.iamService.refreshToken()
          .then((response) => {
            this.isRefreshing = false;
            this.refreshTokenSubject.next(response.accessToken);

            const newRequest = this.addToken(request, response.accessToken);
            next.handle(newRequest).subscribe(
              (event) => observer.next(event),
              (error) => observer.error(error),
              () => observer.complete()
            );
          })
          .catch((error) => {
            this.isRefreshing = false;
            this.iamService.logout();
            observer.error(error);
          });
      });
    } else {
      return this.refreshTokenSubject.pipe(
        filter(token => token != null),
        take(1),
        switchMap(token => {
          return next.handle(this.addToken(request, token!));
        })
      );
    }
  }
}
`;
}

function generateLoginComponent(): string {
  return `import { Component } from '@angular/core';
import { Router } from '@angular/router';
import { IamService } from '../services/iam.service';

@Component({
  selector: 'app-login',
  templateUrl: './login.component.html',
  styleUrls: ['./login.component.css']
})
export class LoginComponent {
  isLoginMode = true;
  email = '';
  password = '';
  firstName = '';
  lastName = '';
  error: string | null = null;
  isLoading = false;

  constructor(
    private iamService: IamService,
    private router: Router
  ) {
    // Redirect if already authenticated
    if (this.iamService.isAuthenticated) {
      this.router.navigate(['/']);
    }
  }

  async onSubmit(): Promise<void> {
    this.error = null;
    this.isLoading = true;

    try {
      if (this.isLoginMode) {
        await this.iamService.login(this.email, this.password);
      } else {
        await this.iamService.register(
          this.email,
          this.password,
          this.firstName,
          this.lastName
        );
      }
      this.router.navigate(['/']);
    } catch (err: any) {
      this.error = err.message || 'Authentication failed';
    } finally {
      this.isLoading = false;
    }
  }

  toggleMode(): void {
    this.isLoginMode = !this.isLoginMode;
    this.error = null;
  }
}
`;
}

function generateLoginTemplate(): string {
  return `<div class="login-container">
  <div class="login-card">
    <h1>{{ isLoginMode ? 'Login' : 'Register' }}</h1>

    <form (ngSubmit)="onSubmit()" #loginForm="ngForm">
      <div *ngIf="!isLoginMode" class="form-group">
        <input
          type="text"
          [(ngModel)]="firstName"
          name="firstName"
          placeholder="First Name"
          required
          class="form-control"
        />
      </div>

      <div *ngIf="!isLoginMode" class="form-group">
        <input
          type="text"
          [(ngModel)]="lastName"
          name="lastName"
          placeholder="Last Name"
          required
          class="form-control"
        />
      </div>

      <div class="form-group">
        <input
          type="email"
          [(ngModel)]="email"
          name="email"
          placeholder="Email"
          required
          class="form-control"
        />
      </div>

      <div class="form-group">
        <input
          type="password"
          [(ngModel)]="password"
          name="password"
          placeholder="Password"
          required
          class="form-control"
        />
      </div>

      <div *ngIf="error" class="error-message">
        {{ error }}
      </div>

      <button
        type="submit"
        [disabled]="!loginForm.form.valid || isLoading"
        class="btn-primary"
      >
        {{ isLoading ? 'Loading...' : (isLoginMode ? 'Login' : 'Register') }}
      </button>
    </form>

    <button (click)="toggleMode()" class="btn-secondary">
      {{ isLoginMode ? 'Need an account? Register' : 'Have an account? Login' }}
    </button>
  </div>
</div>
`;
}

function generateLoginStyle(): string {
  return `.login-container {
  display: flex;
  justify-content: center;
  align-items: center;
  min-height: 100vh;
  background-color: #f5f5f5;
}

.login-card {
  background: white;
  padding: 40px;
  border-radius: 8px;
  box-shadow: 0 2px 10px rgba(0, 0, 0, 0.1);
  width: 100%;
  max-width: 400px;
}

h1 {
  text-align: center;
  margin-bottom: 30px;
  color: #333;
}

.form-group {
  margin-bottom: 20px;
}

.form-control {
  width: 100%;
  padding: 12px;
  border: 1px solid #ddd;
  border-radius: 4px;
  font-size: 14px;
  box-sizing: border-box;
}

.form-control:focus {
  outline: none;
  border-color: #007bff;
}

.btn-primary,
.btn-secondary {
  width: 100%;
  padding: 12px;
  border: none;
  border-radius: 4px;
  font-size: 16px;
  cursor: pointer;
  transition: background-color 0.2s;
}

.btn-primary {
  background-color: #007bff;
  color: white;
  margin-bottom: 10px;
}

.btn-primary:hover:not(:disabled) {
  background-color: #0056b3;
}

.btn-primary:disabled {
  background-color: #ccc;
  cursor: not-allowed;
}

.btn-secondary {
  background-color: transparent;
  color: #007bff;
  border: 1px solid #007bff;
}

.btn-secondary:hover {
  background-color: #f8f9fa;
}

.error-message {
  color: #dc3545;
  margin-bottom: 15px;
  padding: 10px;
  background-color: #f8d7da;
  border: 1px solid #f5c6cb;
  border-radius: 4px;
  font-size: 14px;
}
`;
}
