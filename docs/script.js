const SUPABASE_URL = "https://muefsrcijttbiuahjxwn.supabase.co";
const SUPABASE_ANON_KEY = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Im11ZWZzcmNpanR0Yml1YWhqeHduIiwicm9sZSI6ImFub24iLCJpYXQiOjE3NDc4NjYwNzQsImV4cCI6MjA2MzQ0MjA3NH0.Nzx6my5ZONlYwUQEKrisZIgF5u_IORWoGTwhrbWuh1E";

const supabase = window.supabase.createClient(SUPABASE_URL, SUPABASE_ANON_KEY);

document.addEventListener('DOMContentLoaded', function() {
    const form = document.getElementById('waitlist-form');
    const emailInput = document.getElementById('email');
    const submitButton = form.querySelector('.pulse-button');

    // Add scroll animations
    const observerOptions = {
        threshold: 0.1,
        rootMargin: '0px 0px -50px 0px'
    };

    const observer = new IntersectionObserver((entries) => {
        entries.forEach(entry => {
            if (entry.isIntersecting) {
                entry.target.style.animationPlayState = 'running';
            }
        });
    }, observerOptions);

    // Observe all animated elements
    document.querySelectorAll('.fade-in, .slide-up').forEach(el => {
        observer.observe(el);
    });

    form.addEventListener('submit', async function(e) {
        e.preventDefault();
        
        const email = emailInput.value.trim();
        
        if (email && isValidEmail(email)) {
            console.log('Email submitted:', email);
            
            // Disable button and show loading state
            submitButton.disabled = true;
            const buttonText = submitButton.querySelector('.button-text');
            const originalText = buttonText.textContent;
            buttonText.textContent = 'Joining...';
            
            try {
                // Submit to Supabase
                const result = await submitToSupabase(email);
                
                if (result.success) {
                    // Add button success animation
                    submitButton.classList.add('submitted');
                    
                    // Clear the form
                    emailInput.value = '';
                    
                    console.log('Email successfully added to waitlist!');
                } else {
                    // Handle errors (like duplicate email)
                    console.error('Error adding email:', result.error);
                    showErrorState(result.error);
                }
                
            } catch (error) {
                console.error('Unexpected error:', error);
                showErrorState('Something went wrong. Please try again.');
            }
            
            // Reset button after 3 seconds
            setTimeout(() => {
                submitButton.classList.remove('submitted');
                submitButton.disabled = false;
                buttonText.textContent = originalText;
            }, 3000);
        }
    });

    function isValidEmail(email) {
        const emailRegex = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
        return emailRegex.test(email);
    }

    function showErrorState(message) {
        const buttonText = submitButton.querySelector('.button-text');
        submitButton.style.background = 'linear-gradient(135deg, #ef4444 0%, #f87171 50%, #fca5a5 100%)';
        buttonText.textContent = 'Try Again';
        
        // Reset to normal state after 2 seconds
        setTimeout(() => {
            submitButton.style.background = '';
            buttonText.textContent = 'Join Waitlist';
            submitButton.disabled = false;
        }, 2000);
    }

    // Supabase integration function
    async function submitToSupabase(email) {
        try {
            const { data, error } = await supabase
                .from('waitlist_emails')
                .insert([{ email: email }]);

            if (error) {
                console.error('Supabase error:', error);
                // Check if it's a duplicate email error
                if (error.code === '23505') {
                    return { 
                        success: false, 
                        error: 'Email already registered!' 
                    };
                }
                return { 
                    success: false, 
                    error: error.message 
                };
            }

            return { success: true, data };
            
        } catch (error) {
            console.error('Supabase catch error:', error);
            return { 
                success: false, 
                error: 'Connection error. Please try again.' 
            };
        }
    }

    // Add some interactive effects
    const shapes = document.querySelectorAll('.shape');
    shapes.forEach(shape => {
        shape.addEventListener('mouseenter', () => {
            shape.style.opacity = '0.3';
            shape.style.transform = 'scale(1.2)';
        });
        
        shape.addEventListener('mouseleave', () => {
            shape.style.opacity = '0.1';
            shape.style.transform = 'scale(1)';
        });
    });
});

// Add CSS for fadeOut animation
const style = document.createElement('style');
style.textContent = `
    @keyframes fadeOut {
        to {
            opacity: 0;
            transform: translateY(-10px);
        }
    }
`;
document.head.appendChild(style); 